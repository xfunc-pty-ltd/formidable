using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// The validation engine behind a Formidable form. Owned by a root component (engine lifetime
/// equals component lifetime), it is the single writer to its
/// <see cref="ValidationMessageStore"/>: error-severity issues become EditContext messages
/// (interop with built-in components), while the full issue state (warnings, codes) is exposed
/// via <see cref="GetFieldState"/> and the report objects. Issues resolve to
/// <see cref="FieldIdentifier"/>s through the introspector, so identity follows object
/// instances — reordering or removing collection rows cannot misattribute errors.
/// </summary>
/// <remarks>
/// Validation-result state (issue maps, the store, IsValidating, HasSubmitted) mutates on the
/// renderer's dispatcher and only from the still-current pass, for every validation pass — and
/// synchronously on the calling thread within <see cref="ApplyServerIssues"/>, which is why that
/// method (like <see cref="ValidateForSubmitAsync"/>) documents that it must be called from the
/// renderer's synchronization context. Pass bookkeeping (_version, _passCts, _submitInFlight,
/// _touched) mutates synchronously on the caller's context.
/// </remarks>
public sealed class FormValidationEngine<TModel> : IFormValidationEngine, IDisposable
    where TModel : class
{
    private readonly TModel _model;
    private readonly IModelValidator<TModel> _validator;
    private readonly IModelIntrospector _introspector;
    private readonly FormidableOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Func<Task>, Task> _renderDispatch;
    private readonly ValidationMessageStore _store;
    private readonly EventHandler<FieldChangedEventArgs> _fieldChangedHandler;

    private readonly Dictionary<FieldIdentifier, List<ValidationIssue>> _liveIssues = [];
    private readonly HashSet<FieldIdentifier> _touched = [];
    private Dictionary<FieldIdentifier, List<ValidationIssue>> _submitIssues = [];
    private Dictionary<FieldIdentifier, List<ValidationIssue>> _submitAdvisories = [];
    private HashSet<FieldIdentifier> _submitVisible = [];
    private HashSet<FieldIdentifier> _advisoryVisible = [];
    private List<(FieldIdentifier Field, ValidationIssue Issue)> _appliedServerIssues = [];
    private ValidationIssue? _faultIssue;

    private ITimer? _refreshTimer;
    private CancellationTokenSource? _passCts;
    private bool _submitInFlight;
    private bool _disposed;
    private FieldIdentifier? _validatingScope;

    private int _version;

    /// <summary>Creates an engine bound to one model + edit context pair.</summary>
    public FormValidationEngine(
        TModel model,
        EditContext editContext,
        IModelValidator<TModel> validator,
        IModelIntrospector introspector,
        FormidableOptions options,
        TimeProvider? timeProvider = null,
        Func<Func<Task>, Task>? renderDispatch = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(editContext);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(introspector);
        ArgumentNullException.ThrowIfNull(options);

        _model = model;
        EditContext = editContext;
        _validator = validator;
        _introspector = introspector;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _renderDispatch = renderDispatch ?? (work => work());
        _store = new ValidationMessageStore(editContext);
        _fieldChangedHandler = HandleFieldChanged;
        editContext.OnFieldChanged += _fieldChangedHandler;
        editContext.SetFieldCssClassProvider(new FormidableFieldCssClassProvider(options.CssClasses));
        Registry = new FieldRegistry();
    }

    /// <inheritdoc />
    public EditContext EditContext { get; }

    /// <inheritdoc />
    public FieldRegistry Registry { get; }

    /// <inheritdoc />
    public FormidableOptions Options => _options;

    /// <inheritdoc />
    public bool IsValidating { get; private set; }

    /// <inheritdoc />
    public bool HasSubmitted { get; private set; }

    /// <inheritdoc />
    public event Action? StateChanged;

    /// <inheritdoc />
    public event Action<Exception>? ValidationFaulted;

    /// <inheritdoc />
    public FieldState GetFieldState(FieldIdentifier field)
    {
        var issues = EnumerateIssuesFor(field).ToList();
        return new FieldState(
            IsTouched: _touched.Contains(field),
            IsModified: EditContext.IsModified(field),
            IsValidating: IsValidating && (_validatingScope is null || _validatingScope.Value.Equals(field)),
            HasErrors: issues.Any(i => i.Severity == ValidationSeverity.Error),
            HasWarnings: issues.Any(i => i.Severity == ValidationSeverity.Warning));
    }

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> GetIssues(FieldIdentifier field)
    {
        var result = new List<ValidationIssue>();

        if (_submitIssues.TryGetValue(field, out var submit))
        {
            result.AddRange(submit);
        }

        if (_submitAdvisories.TryGetValue(field, out var advisories))
        {
            result.AddRange(advisories);
        }

        if (_liveIssues.TryGetValue(field, out var live))
        {
            result.AddRange(live.Where(l => !result.Any(existing => existing.Message == l.Message)));
        }

        if (_faultIssue is not null && field.Equals(ModelLevelField))
        {
            result.Add(_faultIssue);
        }

        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<VisibleIssue> GetVisibleIssues()
    {
        var result = new List<VisibleIssue>();

        if (_faultIssue is not null)
        {
            result.Add(new VisibleIssue(ModelLevelField, _faultIssue));
        }

        foreach (var (field, issues) in _submitIssues)
        {
            result.AddRange(issues.Select(issue => new VisibleIssue(field, issue)));
        }

        foreach (var (field, issues) in _submitAdvisories)
        {
            result.AddRange(issues.Select(issue => new VisibleIssue(field, issue)));
        }

        foreach (var (field, issues) in _liveIssues)
        {
            result.AddRange(issues
                .Where(l => !result.Any(v => v.Field.Equals(field) && v.Issue.Message == l.Message))
                .Select(issue => new VisibleIssue(field, issue)));
        }

        return result;
    }

    /// <inheritdoc />
    public void MarkTouched(FieldIdentifier field)
    {
        if (_touched.Add(field))
        {
            NotifyStateChanged();
        }
    }

    /// <summary>The model-level identifier issues with an empty path resolve to.</summary>
    internal FieldIdentifier ModelLevelField => new(_model, string.Empty);

    private void HandleFieldChanged(object? sender, FieldChangedEventArgs e)
    {
        MarkTouched(e.FieldIdentifier);
        _ = RunLivePassAsync(e.FieldIdentifier);
        if (HasSubmitted || _submitInFlight)
        {
            ScheduleRefresh();
        }
    }

    /// <summary>
    /// Cancels and disposes any in-flight pass's <see cref="CancellationTokenSource"/>, then starts a
    /// new one linked to <paramref name="external"/>. Only one pass (live, submit, or refresh) is ever
    /// in flight at a time — starting a new one supersedes whatever came before.
    /// </summary>
    private CancellationToken BeginPass(CancellationToken external)
    {
        _passCts?.Cancel();
        _passCts?.Dispose();
        _passCts = CancellationTokenSource.CreateLinkedTokenSource(external);
        _version++;
        return _passCts.Token;
    }

    /// <summary>
    /// Flips <see cref="IsValidating"/> and notifies, marshaled through <see cref="_renderDispatch"/> so
    /// the flip lands on the renderer's dispatcher rather than on whatever thread completed the pass.
    /// The write is skipped when <paramref name="version"/> no longer matches the current pass —
    /// a superseded pass must not stomp a newer pass's state. <paramref name="scope"/> narrows which
    /// field <see cref="GetFieldState"/> reports as validating: a live pass passes the field that
    /// triggered it; submit and refresh passes pass <see langword="null"/> (form-wide, every field).
    /// Only meaningful when <paramref name="value"/> is <see langword="true"/> — clearing always
    /// clears the scope too.
    /// </summary>
    private Task SetValidating(bool value, int version, FieldIdentifier? scope = null) =>
        _renderDispatch(() =>
        {
            if (version == _version)
            {
                IsValidating = value;
                _validatingScope = value ? scope : null;
                NotifyStateChanged();
            }
            return Task.CompletedTask;
        });

    private async Task RunLivePassAsync(FieldIdentifier changedField)
    {
        if (_submitInFlight)
        {
            return; // submit is the higher-intent operation; live/refresh passes never supersede it
        }

        var version = _version + 1;
        var token = BeginPass(CancellationToken.None);
        await SetValidating(true, version, changedField).ConfigureAwait(false);
        try
        {
            ValidationReport report;
            try
            {
                report = await _validator.ValidateAsync(_model, _options.LiveProfile, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // superseded by a newer pass
            }
            catch (Exception exception)
            {
                await _renderDispatch(() =>
                {
                    if (version == _version)
                    {
                        _faultIssue = new ValidationIssue(
                            string.Empty,
                            "Validation could not run to completion; recent changes may not be fully validated.");
                        RebuildStore();
                    }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

                ValidationFaulted?.Invoke(exception);
                return;
            }

            await _renderDispatch(() =>
            {
                if (version != _version)
                {
                    return Task.CompletedTask; // superseded by a newer pass
                }

                _faultIssue = null;
                _liveIssues[changedField] = report.Issues
                    .Where(issue => Resolve(issue).Equals(changedField))
                    .ToList();
                RebuildStore();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        finally
        {
            await SetValidating(false, version).ConfigureAwait(false);
        }
    }

    private FieldIdentifier Resolve(ValidationIssue issue) =>
        _introspector.Resolve(_model, issue.Path).ToFieldIdentifier(_model, issue.Path);

    private bool IsVisible(ValidationIssue issue, FieldIdentifier field)
    {
        var overridden = _options.DisclosureOverride?.Invoke(issue);
        if (overridden is not null)
        {
            return overridden.Value;
        }

        return field.Equals(ModelLevelField) || Registry.IsRevealed(field);
    }

    private IEnumerable<ValidationIssue> EnumerateIssuesFor(FieldIdentifier field)
    {
        if (_liveIssues.TryGetValue(field, out var live))
        {
            foreach (var issue in live)
            {
                yield return issue;
            }
        }

        if (_submitIssues.TryGetValue(field, out var submit))
        {
            foreach (var issue in submit)
            {
                yield return issue;
            }
        }

        if (_submitAdvisories.TryGetValue(field, out var advisories))
        {
            foreach (var issue in advisories)
            {
                yield return issue;
            }
        }
    }

    private void RebuildStore()
    {
        _store.Clear();

        if (_faultIssue is not null)
        {
            _store.Add(ModelLevelField, _faultIssue.Message);
        }

        foreach (var (field, issues) in _submitIssues)
        {
            foreach (var issue in issues.Where(i => i.Severity == ValidationSeverity.Error))
            {
                _store.Add(field, issue.Message);
            }
        }

        foreach (var (field, issues) in _liveIssues)
        {
            var existing = _submitIssues.TryGetValue(field, out var submit)
                ? submit
                : (IReadOnlyList<ValidationIssue>)[];
            foreach (var issue in issues.Where(i => i.Severity == ValidationSeverity.Error))
            {
                if (!existing.Any(s => s.Message == issue.Message))
                {
                    _store.Add(field, issue.Message);
                }
            }
        }

        EditContext.NotifyValidationStateChanged();
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

    /// <inheritdoc />
    public async Task<SubmitOutcome> ValidateForSubmitAsync(CancellationToken cancellationToken = default)
    {
        var version = _version + 1;
        var token = BeginPass(cancellationToken);
        try
        {
            _submitInFlight = true;
            await SetValidating(true, version).ConfigureAwait(false);
            ValidationReport report;
            try
            {
                report = await _validator.ValidateAsync(_model, _options.SubmitProfile, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Superseded by a newer pass (not the caller's own cancellation) — report blocked quietly.
                return new SubmitOutcome(false, ValidationReport.Empty, []);
            }

            var applied = false;
            var canProceed = false;
            var summary = new List<string>();

            await _renderDispatch(() =>
            {
                if (version != _version)
                {
                    return Task.CompletedTask; // superseded — a newer pass owns engine state now
                }

                applied = true;
                HasSubmitted = true;
                _liveIssues.Clear();
                _faultIssue = null;

                if (report.IsValid)
                {
                    canProceed = true;
                    _submitIssues = [];
                    _submitAdvisories = [];
                    _submitVisible = [];
                    _advisoryVisible = [];
                    _appliedServerIssues = [];
                }
                else
                {
                    var resolvedErrors = report.Errors
                        .Select(issue => (Issue: issue, Field: Resolve(issue)))
                        .ToList();
                    var visibleErrors = resolvedErrors.Where(x => IsVisible(x.Issue, x.Field)).ToList();

                    foreach (var suppressed in resolvedErrors.Where(x => !IsVisible(x.Issue, x.Field)))
                    {
                        System.Diagnostics.Trace.WriteLine(
                            $"Formidable: issue at '{suppressed.Issue.Path}' is suppressed - no rendered field registration matches and no disclosure override applies.");
                        _options.SuppressedIssueDiagnostic?.Invoke(suppressed.Issue);
                    }

                    if (visibleErrors.Count == 0)
                    {
                        // Defensive gate: every failing field is hidden. Block anyway, with a
                        // form-level explanation instead of a silent no-op submit.
                        var gate = new ValidationIssue(
                            string.Empty,
                            "The form cannot be submitted because information that is not currently displayed is invalid.");
                        visibleErrors = [(gate, ModelLevelField)];
                    }

                    _submitIssues = visibleErrors
                        .GroupBy(x => x.Field, x => x.Issue)
                        .ToDictionary(g => g.Key, g => g.ToList());
                    _submitVisible = visibleErrors.Select(x => x.Field).ToHashSet();
                    _appliedServerIssues = [];

                    _submitAdvisories = report.Issues
                        .Where(i => i.Severity != ValidationSeverity.Error)
                        .Select(i => (Issue: i, Field: Resolve(i)))
                        .Where(x => IsVisible(x.Issue, x.Field))
                        .GroupBy(x => x.Field, x => x.Issue)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    // Advisory sites are not necessarily error sites: a visible field can carry a
                    // warning while passing every error rule. Recording them separately is what lets
                    // the refresh pass keep those warnings current instead of dropping them (they are
                    // absent from _submitVisible, which holds error fields only).
                    _advisoryVisible = _submitAdvisories.Keys.ToHashSet();

                    summary = visibleErrors
                        .Select(x => x.Issue.DisplayName ?? x.Issue.Path)
                        .Select(name => name.Length == 0 ? "This form" : name)
                        .Distinct()
                        .ToList();
                }

                RebuildStore();
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            return applied
                ? new SubmitOutcome(canProceed, report, summary)
                : new SubmitOutcome(false, report, []);
        }
        finally
        {
            await _renderDispatch(() =>
            {
                if (version == _version)
                {
                    IsValidating = false;
                    _validatingScope = null;
                    _submitInFlight = false;
                    NotifyStateChanged();
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void ApplyServerIssues(IReadOnlyList<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        HasSubmitted = true;
        _faultIssue = null;

        // The payload is the server's CURRENT verdict, not an addition to its last one: undo
        // exactly what the previous call added before applying this call's issues. ValidationIssue
        // is a record (value equality), so List<T>.Remove takes out one value-equal entry — the
        // instance the previous apply added. If a client-sourced issue happens to be value-identical
        // to a previously-applied server issue, removing either of the two equal entries is
        // indistinguishable and acceptable.
        foreach (var (field, issue) in _appliedServerIssues)
        {
            if (_submitIssues.TryGetValue(field, out var tracked))
            {
                tracked.Remove(issue);
                if (tracked.Count == 0)
                {
                    _submitIssues.Remove(field);
                }
            }
        }

        _appliedServerIssues = [];

        foreach (var group in issues
            .Where(i => i.Severity == ValidationSeverity.Error)
            .Select(i => (Issue: i, Field: Resolve(i)))
            .Where(x => _options.DisclosureOverride?.Invoke(x.Issue) != false) // server-declared: bypass registry
            .GroupBy(x => x.Field, x => x.Issue))
        {
            var field = group.Key;
            if (_submitIssues.TryGetValue(field, out var existing))
            {
                existing.AddRange(group);
            }
            else
            {
                _submitIssues[field] = [.. group];
            }

            _submitVisible.Add(field); // sticky: reveal state never un-reveals a field

            foreach (var issue in group)
            {
                _appliedServerIssues.Add((field, issue));
            }
        }

        RebuildStore();
    }

    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            // A refresh pass whose dispatch was still queued when the owning component went away
            // re-arms the timer from its own deferral branch; re-arming a disposed ITimer throws.
            return;
        }

        _refreshTimer ??= _timeProvider.CreateTimer(
            _ => _ = _renderDispatch(RunRefreshPassAsync),
            state: null,
            dueTime: Timeout.InfiniteTimeSpan,
            period: Timeout.InfiniteTimeSpan);
        _refreshTimer.Change(_options.RefreshDebounce, Timeout.InfiniteTimeSpan);
    }

    private async Task RunRefreshPassAsync()
    {
        if (_submitInFlight)
        {
            ScheduleRefresh(); // defer and re-arm — the edit must still be revalidated once submit finishes
            return;
        }

        var version = _version + 1;
        var token = BeginPass(CancellationToken.None);
        await SetValidating(true, version).ConfigureAwait(false);
        try
        {
            ValidationReport report;
            try
            {
                report = await _validator.ValidateAsync(_model, _options.SubmitProfile, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // superseded by a newer pass
            }
            catch (Exception exception)
            {
                await _renderDispatch(() =>
                {
                    if (version == _version)
                    {
                        _faultIssue = new ValidationIssue(
                            string.Empty,
                            "Validation could not run to completion; recent changes may not be fully validated.");
                        RebuildStore();
                    }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);

                ValidationFaulted?.Invoke(exception);
                return;
            }

            await _renderDispatch(() =>
            {
                if (version != _version)
                {
                    return Task.CompletedTask; // superseded by a newer pass
                }

                _faultIssue = null;

                // Resurface only what the user already saw at submit AND is still failing —
                // fixed fields clear; fields revealed after submit stay quiet until the next submit.
                _submitIssues = report.Errors
                    .Select(issue => (Issue: issue, Field: Resolve(issue)))
                    .Where(x => _submitVisible.Contains(x.Field))
                    .GroupBy(x => x.Field, x => x.Issue)
                    .ToDictionary(g => g.Key, g => g.ToList());
                _appliedServerIssues = [];

                // Advisories follow the same "only what the user already saw" rule, but over the
                // union of the two submit-time sets: a field that was an error site keeps any
                // warning it also picked up, and a field that was only ever an advisory site keeps
                // its warning refreshed instead of disappearing on the first unrelated edit.
                _submitAdvisories = report.Issues
                    .Where(i => i.Severity != ValidationSeverity.Error)
                    .Select(issue => (Issue: issue, Field: Resolve(issue)))
                    .Where(x => _submitVisible.Contains(x.Field) || _advisoryVisible.Contains(x.Field))
                    .GroupBy(x => x.Field, x => x.Issue)
                    .ToDictionary(g => g.Key, g => g.ToList());

                RebuildStore();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        finally
        {
            await SetValidating(false, version).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        EditContext.OnFieldChanged -= _fieldChangedHandler;
        _refreshTimer?.Dispose();
        _passCts?.Cancel();
        _passCts?.Dispose();
        _store.Clear();
        EditContext.NotifyValidationStateChanged();
    }
}
