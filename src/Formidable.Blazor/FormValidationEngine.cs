using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;

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
/// _liveVersion, _touched, _pendingRefreshFields, _pendingLiveFields) mutates synchronously on the
/// caller's context — except on the dispatcher for: _pendingRefreshFields, when RunRefreshPassAsync
/// snapshots and clears it at the start of a refresh pass; _pendingLiveFields, when a live pass
/// clears it after writing its verdicts; and the two in-flight markers _submitInFlight and
/// _liveVersion, which the pass that set them clears alongside IsValidating.
/// </remarks>
public sealed class FormValidationEngine<TModel> : IFormValidationEngine, IValidatingFieldReader, IDisposable
    where TModel : class
{
    private readonly TModel _model;
    private readonly IModelValidator<TModel> _validator;
    private readonly IModelIntrospector _introspector;
    private readonly FormidableOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Func<Task>, Task> _renderDispatch;
    private readonly ILogger? _logger;
    private readonly ValidationMessageStore _store;
    private readonly EventHandler<FieldChangedEventArgs> _fieldChangedHandler;

    private readonly Dictionary<FieldIdentifier, List<ValidationIssue>> _liveIssues = [];
    private readonly HashSet<FieldIdentifier> _touched = [];
    private readonly HashSet<FieldIdentifier> _pendingRefreshFields = [];
    private readonly HashSet<FieldIdentifier> _pendingLiveFields = [];
    private Dictionary<FieldIdentifier, List<ValidationIssue>> _submitIssues = [];
    private Dictionary<FieldIdentifier, List<ValidationIssue>> _submitAdvisories = [];
    private HashSet<FieldIdentifier> _submitVisible = [];
    private HashSet<FieldIdentifier> _advisoryVisible = [];
    private List<(FieldIdentifier Field, ValidationIssue Issue)> _appliedServerIssues = [];
    private ValidationIssue? _faultIssue;

    /// <summary>The result every issue read shares when a field has nothing to say.</summary>
    private static readonly IReadOnlyList<ValidationIssue> NoIssues = [];

    private ITimer? _refreshTimer;
    private CancellationTokenSource? _passCts;
    private bool _submitInFlight;
    private bool _disposed;
    private HashSet<FieldIdentifier>? _validatingScope;

    private int _version;

    // The pass version of the live pass in flight; -1 when none is. Held as a version rather than
    // as a plain in-flight flag so it cannot go stale: a submit supersedes a live pass without
    // waiting for it, and a flag the dying pass then declined to clear — it is no longer the
    // current pass, so it must not write state — would leave every later refresh deferring to a
    // pass that ended long ago.
    private int _liveVersion = -1;

    /// <summary>
    /// Creates an engine bound to one model + edit context pair. <paramref name="logger"/> is
    /// optional — a direct construction with none supplied gets the engine's other diagnostics
    /// (Trace, <see cref="FormidableOptions.SuppressedIssueDiagnostic"/>) unaffected.
    /// </summary>
    public FormValidationEngine(
        TModel model,
        EditContext editContext,
        IModelValidator<TModel> validator,
        IModelIntrospector introspector,
        FormidableOptions options,
        TimeProvider? timeProvider = null,
        Func<Func<Task>, Task>? renderDispatch = null,
        ILogger? logger = null)
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
        _logger = logger;
        _store = new ValidationMessageStore(editContext);
        _fieldChangedHandler = HandleFieldChanged;
        editContext.OnFieldChanged += _fieldChangedHandler;
        editContext.SetFieldCssClassProvider(new FormidableFieldCssClassProvider(options.CssClasses, this));
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
        // Every input, every field wrapper and the css class provider read this on every
        // notification round, so it answers its two questions from one walk of the channels that
        // hold anything for the field, and allocates nothing to do it.
        var hasErrors = false;
        var hasWarnings = false;

        if (_liveIssues.TryGetValue(field, out var live))
        {
            ScanSeverities(live, ref hasErrors, ref hasWarnings);
        }

        if (!(hasErrors && hasWarnings) && _submitIssues.TryGetValue(field, out var submit))
        {
            ScanSeverities(submit, ref hasErrors, ref hasWarnings);
        }

        if (!(hasErrors && hasWarnings) && _submitAdvisories.TryGetValue(field, out var advisories))
        {
            ScanSeverities(advisories, ref hasErrors, ref hasWarnings);
        }

        return new FieldState(
            IsTouched: _touched.Contains(field),
            IsModified: EditContext.IsModified(field),
            IsValidating: IsFieldValidating(field),
            HasErrors: hasErrors,
            HasWarnings: hasWarnings);
    }

    /// <summary>
    /// Whether a validation pass in flight currently covers <paramref name="field"/> — form-wide
    /// for a submit pass, scoped to the changed field for a live pass, scoped to the fields edited
    /// within the debounce window for a refresh pass. <see cref="GetFieldState"/> folds this into
    /// its own read; <see cref="IValidatingFieldReader"/> exposes it standalone for a caller (the
    /// css class provider) that wants only this and not the severity scan the rest of
    /// <see cref="FieldState"/> costs.
    /// </summary>
    private bool IsFieldValidating(FieldIdentifier field) =>
        IsValidating && (_validatingScope is null || _validatingScope.Contains(field));

    /// <inheritdoc cref="IValidatingFieldReader.IsFieldValidating"/>
    bool IValidatingFieldReader.IsFieldValidating(FieldIdentifier field) => IsFieldValidating(field);

    /// <inheritdoc />
    /// <remarks>
    /// Ordering is part of what a message list renders: the submit channel first (errors, then
    /// advisories), then the live channel minus whatever it would repeat, and the fault issue last
    /// — a fault is about the pass rather than the field, so it trails the field's own verdict.
    /// </remarks>
    public IReadOnlyList<ValidationIssue> GetIssues(FieldIdentifier field)
    {
        var hasSubmit = _submitIssues.TryGetValue(field, out var submit);
        var hasAdvisories = _submitAdvisories.TryGetValue(field, out var advisories);
        var hasLive = _liveIssues.TryGetValue(field, out var live);
        var fault = _faultIssue is not null && field.Equals(ModelLevelField) ? _faultIssue : null;

        if (!hasSubmit && !hasAdvisories && !hasLive && fault is null)
        {
            return NoIssues;
        }

        var result = new List<ValidationIssue>();
        var showing = new HashSet<string>(StringComparer.Ordinal);

        if (hasSubmit)
        {
            AddShowing(result, showing, submit!);
        }

        if (hasAdvisories)
        {
            AddShowing(result, showing, advisories!);
        }

        if (hasLive)
        {
            result.AddRange(ExceptShadowed(live!, showing));
        }

        if (fault is not null)
        {
            result.Add(fault);
        }

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Channel-major rather than field-major, because the summary groups by severity and the order
    /// within a group is the order issues arrive here: the fault issue first, then every field's
    /// submit errors, then every field's advisories, then the live channel minus whatever it would
    /// repeat for the same field.
    /// </remarks>
    public IReadOnlyList<VisibleIssue> GetVisibleIssues()
    {
        var result = new List<VisibleIssue>();

        // Only the live phase consults the shadow map, so it exists only when there is a live phase
        // to consult it — a summary showing submit issues alone builds nothing.
        var showing = _liveIssues.Count > 0 ? new Dictionary<FieldIdentifier, HashSet<string>>() : null;

        if (_faultIssue is not null)
        {
            result.Add(new VisibleIssue(ModelLevelField, _faultIssue));
            RecordShowing(showing, ModelLevelField, _faultIssue);
        }

        foreach (var (field, issues) in _submitIssues)
        {
            foreach (var issue in issues)
            {
                result.Add(new VisibleIssue(field, issue));
                RecordShowing(showing, field, issue);
            }
        }

        foreach (var (field, issues) in _submitAdvisories)
        {
            foreach (var issue in issues)
            {
                result.Add(new VisibleIssue(field, issue));
                RecordShowing(showing, field, issue);
            }
        }

        if (showing is not null)
        {
            foreach (var (field, issues) in _liveIssues)
            {
                foreach (var issue in ExceptShadowed(issues, ShowingFor(showing, field)))
                {
                    result.Add(new VisibleIssue(field, issue));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Answers whether <paramref name="issues"/> carries an error and whether it carries a warning,
    /// stopping the moment both are answered.
    /// </summary>
    private static void ScanSeverities(List<ValidationIssue> issues, ref bool hasErrors, ref bool hasWarnings)
    {
        foreach (var issue in issues)
        {
            if (issue.Severity == ValidationSeverity.Error)
            {
                hasErrors = true;
            }
            else if (issue.Severity == ValidationSeverity.Warning)
            {
                hasWarnings = true;
            }

            if (hasErrors && hasWarnings)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Adds every issue to <paramref name="result"/> and records its message as already showing for
    /// the field, which is what the live channel is then filtered against.
    /// </summary>
    private static void AddShowing(
        List<ValidationIssue> result,
        HashSet<string> showing,
        List<ValidationIssue> issues)
    {
        foreach (var issue in issues)
        {
            result.Add(issue);
            showing.Add(issue.Message);
        }
    }

    /// <summary>
    /// The one shadow rule behind every merged issue read: a live-channel issue is dropped when the
    /// same message is already showing for the same field, so a rule that fails in both channels
    /// reads as one message rather than two. <paramref name="showing"/> grows as issues pass, which
    /// is also what collapses two live issues carrying the same message into one.
    /// </summary>
    private static IEnumerable<ValidationIssue> ExceptShadowed(
        List<ValidationIssue> issues,
        HashSet<string> showing)
    {
        foreach (var issue in issues)
        {
            if (showing.Add(issue.Message))
            {
                yield return issue;
            }
        }
    }

    /// <summary>Records one message as showing for a field, when a shadow map is being kept.</summary>
    private static void RecordShowing(
        Dictionary<FieldIdentifier, HashSet<string>>? showing,
        FieldIdentifier field,
        ValidationIssue issue)
    {
        if (showing is not null)
        {
            ShowingFor(showing, field).Add(issue.Message);
        }
    }

    /// <summary>The messages already showing for one field, created on first use.</summary>
    private static HashSet<string> ShowingFor(
        Dictionary<FieldIdentifier, HashSet<string>> showing,
        FieldIdentifier field)
    {
        if (!showing.TryGetValue(field, out var messages))
        {
            showing[field] = messages = new HashSet<string>(StringComparer.Ordinal);
        }

        return messages;
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
            _pendingRefreshFields.Add(e.FieldIdentifier);
            ScheduleRefresh();
        }
    }

    /// <summary>
    /// Whether the pass currently in flight is a live pass — true from the moment a live pass takes
    /// the current version until it finishes, and false again as soon as any newer pass takes that
    /// version from it.
    /// </summary>
    private bool LiveInFlight => _liveVersion == _version;

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
    /// fields <see cref="GetFieldState"/> reports as validating: a live pass passes the single field
    /// that triggered it; a refresh pass passes the fields edited within its debounce window; a
    /// submit pass passes <see langword="null"/> (form-wide, every field). Only meaningful when
    /// <paramref name="value"/> is <see langword="true"/> — clearing always clears the scope too.
    /// Also raises the EditContext's own validation-state notification, not just the engine's: a
    /// native InputBase re-renders on that event, not on <see cref="StateChanged"/>, so without it
    /// the Pending class a native input picks up through
    /// <see cref="FormidableFieldCssClassProvider"/> would light on the next store rebuild but have
    /// no later trigger to clear it once the pass ends.
    /// </summary>
    private Task SetValidating(bool value, int version, HashSet<FieldIdentifier>? scope = null) =>
        _renderDispatch(() =>
        {
            if (version == _version)
            {
                IsValidating = value;
                _validatingScope = value ? scope : null;
                NotifyStateChanged();
                EditContext.NotifyValidationStateChanged();
            }
            return Task.CompletedTask;
        });

    private async Task RunLivePassAsync(FieldIdentifier changedField)
    {
        if (_submitInFlight)
        {
            return; // submit is the higher-intent operation; live/refresh passes never supersede it
        }

        _pendingLiveFields.Add(changedField);
        var version = _version + 1;
        var token = BeginPass(CancellationToken.None);
        _liveVersion = version;
        await SetValidating(true, version, [changedField]).ConfigureAwait(false);
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

                // Every field whose pass this one superseded, not just the field that started it:
                // each live pass validates the whole model under the same LiveProfile, so this
                // report answers for those fields too. A superseded pass writes nothing — it is no
                // longer current — so without this the field it was answering for would keep a
                // stale verdict, or none at all, until something else happened to revalidate it. A
                // field the report says nothing about gets an empty verdict, not a skipped one.
                var byField = GroupByResolvedField(report.Issues);
                foreach (var field in _pendingLiveFields)
                {
                    _liveIssues[field] = byField.TryGetValue(field, out var forField) ? forField : [];
                }

                _pendingLiveFields.Clear();

                // The pass ends here, not only in the finally below: clearing the flag, the scope
                // and the in-flight marker before RebuildStore's notification means the verdict and
                // the cleared pending indicator reach every subscriber in one round instead of two
                // back-to-back ones — and the marker still clears before any notification, so a
                // handler that reacts by letting a deferred refresh run cannot see this pass as the
                // one in flight.
                IsValidating = false;
                _validatingScope = null;
                _liveVersion = -1;

                RebuildStore();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        finally
        {
            // The paths that never reach the verdict dispatch — a superseded pass, a faulted one —
            // end here instead, and this no-ops once the dispatch has ended the pass itself.
            // What SetValidating does, plus the in-flight marker — cleared in the same dispatch and
            // before the notification, exactly as the submit pass clears _submitInFlight, so that a
            // handler which reacts by letting a deferred refresh run cannot still see this pass as
            // the one in flight. Also raises EditContext.NotifyValidationStateChanged() itself
            // (inlined rather than routed through SetValidating, which does the same) so a native
            // InputBase — which re-renders on that event, not on StateChanged — clears the Pending
            // class this pass's own start already gave it.
            if (IsValidating)
            {
                await _renderDispatch(() =>
                {
                    if (version == _version)
                    {
                        IsValidating = false;
                        _validatingScope = null;
                        _liveVersion = -1;
                        NotifyStateChanged();
                        EditContext.NotifyValidationStateChanged();
                    }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
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

    /// <summary>
    /// The submit report's non-error issues, resolved and filtered to visible fields, grouped by
    /// field. Used identically by both branches of <see cref="ValidateForSubmitAsync"/> — whether
    /// or not the same report also contains errors has no bearing on which of its advisories are
    /// disclosed.
    /// </summary>
    private Dictionary<FieldIdentifier, List<ValidationIssue>> ResolveVisibleAdvisories(ValidationReport report) =>
        report.Issues
            .Where(i => i.Severity != ValidationSeverity.Error)
            .Select(i => (Issue: i, Field: Resolve(i)))
            .Where(x => IsVisible(x.Issue, x.Field))
            .GroupBy(x => x.Field, x => x.Issue)
            .ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>
    /// Resolves each issue to its field exactly once and groups by the result — one path parse and
    /// object walk per issue, rather than one per issue for every field waiting on the verdict.
    /// </summary>
    private Dictionary<FieldIdentifier, List<ValidationIssue>> GroupByResolvedField(
        IReadOnlyList<ValidationIssue> issues)
    {
        var grouped = new Dictionary<FieldIdentifier, List<ValidationIssue>>();

        foreach (var issue in issues)
        {
            var field = Resolve(issue);
            if (!grouped.TryGetValue(field, out var forField))
            {
                grouped[field] = forField = [];
            }

            forField.Add(issue);
        }

        return grouped;
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
            // Deliberately not the shadow rule the issue reads share: the against-list here is this
            // field's submit issues alone and never grows, so two live errors carrying the same
            // message both reach the store, where ExceptShadowed would collapse them to one. The
            // store is the interop surface a native ValidationMessage/ValidationSummary renders
            // straight out, so narrowing the merge here would change what those components show
            // rather than what an engine read returns.
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

            var canProceed = false;
            var summary = new List<string>();

            await _renderDispatch(() =>
            {
                if (version != _version)
                {
                    // Superseded — a newer pass owns engine state now, and the empty locals above
                    // are what a pass that wrote nothing has to report.
                    return Task.CompletedTask;
                }

                HasSubmitted = true;
                _liveIssues.Clear();

                // Submit takes the live channel over wholesale, so a live pass it superseded has
                // nothing left to hand on: its field's verdict is this report's, and any further
                // edit is revalidated by the refresh that edit arms.
                _pendingLiveFields.Clear();
                _faultIssue = null;

                if (report.IsValid)
                {
                    // No error-severity issues, so nothing blocks the submit - but the report can
                    // still carry warnings/infos (e.g. an error-free field with an advisory-severity
                    // rule). Warning lifetime is symmetric with errors: submit is the disclosure
                    // event regardless of which severity it reveals, so advisories are captured here
                    // exactly as the invalid branch below captures them, even though there is no
                    // error to pair them with.
                    canProceed = true;
                    _submitIssues = [];
                    _submitVisible = [];
                    _appliedServerIssues = [];

                    _submitAdvisories = ResolveVisibleAdvisories(report);
                    _advisoryVisible = _submitAdvisories.Keys.ToHashSet();
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
                        _logger?.LogWarning(
                            "Formidable: issue at '{Path}' is suppressed - no rendered field registration matches and no disclosure override applies.",
                            suppressed.Issue.Path);
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

                    _submitAdvisories = ResolveVisibleAdvisories(report);

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

                // The pass ends here, not only in the finally below: clearing the flag, the scope
                // and the in-flight marker before RebuildStore's notification means the verdict and
                // the cleared pending indicator reach every subscriber in one round instead of two
                // back-to-back ones — and the marker still clears before any notification, so a
                // handler that reacts by letting a deferred refresh run cannot see this pass as the
                // one in flight.
                IsValidating = false;
                _validatingScope = null;
                _submitInFlight = false;

                RebuildStore();
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            return new SubmitOutcome(canProceed, report, summary);
        }
        finally
        {
            // The paths that never reach the verdict dispatch — a superseded pass, a cancelled one
            // — end here instead, and this no-ops once the dispatch has ended the pass itself.
            // Inlined rather than routed through SetValidating (which does the same) so the
            // in-flight marker clears in the same dispatch; also raises
            // EditContext.NotifyValidationStateChanged() so a native InputBase — which re-renders
            // on that event, not on StateChanged — clears the Pending class this pass's own start
            // already gave it.
            if (IsValidating)
            {
                await _renderDispatch(() =>
                {
                    if (version == _version)
                    {
                        IsValidating = false;
                        _validatingScope = null;
                        _submitInFlight = false;
                        NotifyStateChanged();
                        EditContext.NotifyValidationStateChanged();
                    }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public void ApplyServerIssues(IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        HasSubmitted = true;
        _faultIssue = null;

        // The payload is the server's CURRENT verdict, not an addition to its last one: undo
        // exactly what the previous call added before applying this call's issues. ValidationIssue
        // is a record (value equality), so List<T>.Remove takes out one value-equal entry — the
        // instance the previous apply added, or (see RunRefreshPassAsync) the message-matched
        // refreshed issue standing in for it if a refresh landed since. If a client-sourced issue
        // happens to be value-identical to a previously-applied server issue, removing either of
        // the two equal entries is indistinguishable and acceptable.
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
        if (_submitInFlight || LiveInFlight)
        {
            // Defer and re-arm — the edit must still be revalidated once the pass in flight
            // finishes. Submit is the higher-intent operation and is never superseded; a live pass
            // is waited out for a different reason: starting here would cancel it (see BeginPass)
            // and this pass would then discard its own verdict for any field that was not an error
            // site at the last submit, so a single edit's answer would be lost on both channels at
            // once. Waiting can in principle be starved by passes that never quiesce — the same
            // exposure the submit case has always carried, and a form whose passes never settle
            // has no moment at which a refresh would be meaningful anyway.
            ScheduleRefresh();
            return;
        }

        var version = _version + 1;
        var token = BeginPass(CancellationToken.None);

        // Snapshot-and-clear: this window's refresh flags exactly the fields the user edited
        // since the last refresh (or since submit, for the first one). Fields edited while this
        // pass is in flight land in the now-empty accumulator and are flagged by the NEXT
        // refresh instead — they are not lost, just deferred one window (see ScheduleRefresh's
        // re-arm on the _submitInFlight branch above for the analogous deferred case). If THIS
        // pass is itself superseded before finishing (a live pass never defers to a refresh —
        // see BeginPass), its already-captured scope is deliberately dropped, not merged into
        // whatever runs next: the superseding pass owns the indicator outright, exactly as one
        // live pass already displaces another's scope pre-submit — this is the same "post-submit
        // editing reads like pre-submit editing" symmetry, not a gap.

        // The empty case is reachable, not a bug: a re-armed timer (see the _submitInFlight defer
        // above) can fire after an earlier refresh pass already snapshotted the union, leaving
        // nothing new accumulated. The ternary's null branch then falls back to form-wide
        // validating for this redundant pass — a brief conservative flash, the pre-scoping
        // behaviour, never a stuck flag.
        var scope = _pendingRefreshFields.Count > 0 ? new HashSet<FieldIdentifier>(_pendingRefreshFields) : null;
        _pendingRefreshFields.Clear();

        await SetValidating(true, version, scope).ConfigureAwait(false);
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

                // The previous ApplyServerIssues call's bookkeeping, captured before the refresh's
                // own issues (below) replace _submitIssues wholesale.
                var previouslyApplied = _appliedServerIssues;

                // Resurface only what the user already saw at submit AND is still failing —
                // fixed fields clear; fields revealed after submit stay quiet until the next submit.
                _submitIssues = report.Errors
                    .Select(issue => (Issue: issue, Field: Resolve(issue)))
                    .Where(x => _submitVisible.Contains(x.Field))
                    .GroupBy(x => x.Field, x => x.Issue)
                    .ToDictionary(g => g.Key, g => g.ToList());

                // Re-key by MESSAGE, not by field: a field can carry both a server-applied issue
                // and an unrelated, independently-failing client-sourced submit issue at once (see
                // ApplyServerIssues' own remarks), and adopting everything the refresh wrote for a
                // previously-applied field would sweep up that unrelated client issue too, so the
                // next apply would delete it — a worse bug than the duplicate this guards against.
                // Instead, for each issue the previous apply is responsible for, adopt the
                // refreshed issue on that field whose message matches it (the duplicate this fixes
                // is by definition message-identical, so this still finds and replaces it) and drop
                // the bookkeeping entry when no refreshed issue matches — the server's contribution
                // is no longer part of what's shown, so there is nothing left to protect.
                _appliedServerIssues = previouslyApplied
                    .Select(entry => (
                        entry.Field,
                        Issue: _submitIssues.TryGetValue(entry.Field, out var current)
                            ? current.FirstOrDefault(i => i.Message == entry.Issue.Message)
                            : null))
                    .Where(x => x.Issue is not null)
                    .Select(x => (x.Field, Issue: x.Issue!))
                    .ToList();

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

                // The pass ends here, not only in the finally below: clearing the flag and the
                // scope before RebuildStore's notification means the refreshed verdict and the
                // cleared pending indicator reach every subscriber in one round instead of two
                // back-to-back ones.
                IsValidating = false;
                _validatingScope = null;

                RebuildStore();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        finally
        {
            // The paths that never reach the verdict dispatch — a superseded pass, a faulted one —
            // end here instead, and this no-ops once the dispatch has ended the pass itself.
            if (IsValidating)
            {
                await SetValidating(false, version).ConfigureAwait(false);
            }
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
