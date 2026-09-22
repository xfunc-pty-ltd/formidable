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
    private HashSet<FieldIdentifier> _submitVisible = [];

    private ITimer? _refreshTimer;
    private CancellationTokenSource? _passCts;

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
        Registry = new FieldRegistry();
    }

    /// <inheritdoc />
    public EditContext EditContext { get; }

    /// <inheritdoc />
    public FieldRegistry Registry { get; }

    /// <inheritdoc />
    public bool IsValidating { get; private set; }

    /// <inheritdoc />
    public bool HasSubmitted { get; private set; }

    /// <inheritdoc />
    public event Action? StateChanged;

    /// <inheritdoc />
    public FieldState GetFieldState(FieldIdentifier field)
    {
        var issues = EnumerateIssuesFor(field).ToList();
        return new FieldState(
            IsTouched: _touched.Contains(field),
            IsModified: EditContext.IsModified(field),
            IsValidating: IsValidating,
            HasErrors: issues.Any(i => i.Severity == ValidationSeverity.Error),
            HasWarnings: issues.Any(i => i.Severity == ValidationSeverity.Warning));
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
        if (HasSubmitted)
        {
            ScheduleRefresh(); // implemented in Task 5
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
    /// </summary>
    private Task SetValidating(bool value) =>
        _renderDispatch(() =>
        {
            IsValidating = value;
            NotifyStateChanged();
            return Task.CompletedTask;
        });

    private async Task RunLivePassAsync(FieldIdentifier changedField)
    {
        var version = _version + 1;
        var token = BeginPass(CancellationToken.None);
        await SetValidating(true).ConfigureAwait(false);
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

            if (version != _version)
            {
                return; // superseded by a newer pass
            }

            await _renderDispatch(() =>
            {
                _liveIssues[changedField] = report.Issues
                    .Where(issue => Resolve(issue).Equals(changedField))
                    .ToList();
                RebuildStore();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        finally
        {
            await SetValidating(false).ConfigureAwait(false);
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
    }

    private void RebuildStore()
    {
        _store.Clear();

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
        await SetValidating(true).ConfigureAwait(false);
        try
        {
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

            if (version != _version)
            {
                return new SubmitOutcome(false, report, []);
            }

            HasSubmitted = true;
            _liveIssues.Clear();

            if (report.IsValid)
            {
                _submitIssues = [];
                _submitVisible = [];
                await _renderDispatch(() => { RebuildStore(); return Task.CompletedTask; }).ConfigureAwait(false);
                return new SubmitOutcome(true, report, []);
            }

            var resolvedErrors = report.Errors
                .Select(issue => (Issue: issue, Field: Resolve(issue)))
                .ToList();
            var visibleErrors = resolvedErrors.Where(x => IsVisible(x.Issue, x.Field)).ToList();

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

            var summary = visibleErrors
                .Select(x => x.Issue.DisplayName ?? x.Issue.Path)
                .Select(name => name.Length == 0 ? "This form" : name)
                .Distinct()
                .ToList();

            await _renderDispatch(() => { RebuildStore(); return Task.CompletedTask; }).ConfigureAwait(false);
            return new SubmitOutcome(false, report, summary);
        }
        finally
        {
            await SetValidating(false).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void ApplyServerIssues(IReadOnlyList<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        HasSubmitted = true;

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

            _submitVisible.Add(field);
        }

        RebuildStore();
    }

    private void ScheduleRefresh()
    {
        _refreshTimer ??= _timeProvider.CreateTimer(
            _ => _ = _renderDispatch(RunRefreshPassAsync),
            state: null,
            dueTime: Timeout.InfiniteTimeSpan,
            period: Timeout.InfiniteTimeSpan);
        _refreshTimer.Change(_options.RefreshDebounce, Timeout.InfiniteTimeSpan);
    }

    private async Task RunRefreshPassAsync()
    {
        var version = _version + 1;
        var token = BeginPass(CancellationToken.None);
        await SetValidating(true).ConfigureAwait(false);
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

            if (version != _version)
            {
                return;
            }

            // Resurface only what the user already saw at submit AND is still failing —
            // fixed fields clear; fields revealed after submit stay quiet until the next submit.
            _submitIssues = report.Errors
                .Select(issue => (Issue: issue, Field: Resolve(issue)))
                .Where(x => _submitVisible.Contains(x.Field))
                .GroupBy(x => x.Field, x => x.Issue)
                .ToDictionary(g => g.Key, g => g.ToList());
            RebuildStore();
        }
        finally
        {
            await SetValidating(false).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        EditContext.OnFieldChanged -= _fieldChangedHandler;
        _refreshTimer?.Dispose();
        _passCts?.Cancel();
        _passCts?.Dispose();
        _store.Clear();
        EditContext.NotifyValidationStateChanged();
    }
}
