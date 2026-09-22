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
/// renderer's synchronization context. Pass bookkeeping (_version, _passCts, _currentPass,
/// _touched, _pendingRefreshFields, _pendingLiveFields) mutates synchronously on the caller's
/// context — except on the dispatcher for: _pendingRefreshFields, when a refresh pass snapshots
/// and clears it as it begins; _pendingLiveFields, when a live pass clears it after writing its
/// verdicts; and _currentPass, which the pass that recorded it clears alongside IsValidating.
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
    private bool _disposed;
    private HashSet<FieldIdentifier>? _validatingScope;

    private int _version;

    // The pass in flight, or null when none is. One descriptor rather than a flag per kind so it
    // cannot go stale: every pass records itself here as it begins, a newer pass overwrites that
    // record outright, and only a pass that is still the current one ever clears it. A pass that
    // was superseded and therefore declined to write state — it is no longer current, so it must
    // not — cannot leave the engine deferring to a pass that ended long ago.
    private PassScope? _currentPass;

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

    /// <inheritdoc cref="IValidatingFieldReader.IsFieldTouched"/>
    bool IValidatingFieldReader.IsFieldTouched(FieldIdentifier field) => _touched.Contains(field);

    /// <inheritdoc />
    /// <remarks>
    /// Ordering is part of what a message list renders: the submit channel first (errors, then
    /// advisories), then the live channel, and the fault issue last — a fault is about the pass
    /// rather than the field, so it trails the field's own verdict. Everything after the errors is
    /// filtered against what is already showing, so a message a later channel repeats — a server
    /// response echoing an advisory the client already disclosed, a live rule failing the same way
    /// twice — reads once, in the position the first channel to say it gave it.
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
            result.AddRange(ExceptShadowed(advisories!, showing));
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
    /// submit errors, then every field's advisories, then the live channel — each channel after the
    /// errors minus whatever is already showing for the same field, exactly as
    /// <see cref="GetIssues"/> filters them.
    /// </remarks>
    public IReadOnlyList<VisibleIssue> GetVisibleIssues()
    {
        var result = new List<VisibleIssue>();

        // Only the filtered phases consult the shadow map, so it exists only when there is one to
        // consult it — a summary showing submit errors alone builds nothing.
        var showing = _liveIssues.Count > 0 || _submitAdvisories.Count > 0
            ? new Dictionary<FieldIdentifier, HashSet<string>>()
            : null;

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
            foreach (var issue in ExceptShadowed(issues, ShowingFor(showing!, field)))
            {
                result.Add(new VisibleIssue(field, issue));
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
    /// The one shadow rule behind every merged issue read: an issue is dropped when the same
    /// message is already showing for the same field, so a rule that fails in more than one channel
    /// reads as one message rather than several. Submit errors are the against-list every other
    /// channel is filtered by; the advisory channel is filtered too, which is what collapses a
    /// server response echoing an advisory the client already disclosed — the client's copy is
    /// there first, so the client's copy is the one that shows. <paramref name="showing"/> grows as
    /// issues pass, which is also what collapses two issues in one channel carrying the same
    /// message into one.
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
        if (HasSubmitted || SubmitInFlight)
        {
            _pendingRefreshFields.Add(e.FieldIdentifier);
            ScheduleRefresh();
        }
    }

    /// <summary>
    /// Whether the pass currently in flight is a live pass — true from the moment a live pass
    /// becomes the current one until it ends, and false again as soon as any newer pass takes that
    /// place from it.
    /// </summary>
    private bool LiveInFlight => _currentPass?.Kind == PassKind.Live;

    /// <summary>
    /// Whether the pass currently in flight is a submit. Live and refresh passes both read this and
    /// stand down — submit is the higher-intent operation, and neither ever supersedes it.
    /// </summary>
    private bool SubmitInFlight => _currentPass?.Kind == PassKind.Submit;

    /// <summary>
    /// Cancels and disposes any in-flight pass's <see cref="CancellationTokenSource"/>, then starts a
    /// new one linked to <paramref name="external"/> and records the new pass as the current one.
    /// Only one pass (live, submit, or refresh) is ever in flight at a time — starting a new one
    /// supersedes whatever came before, which is exactly what taking over the descriptor means.
    /// </summary>
    private PassScope BeginPass(PassKind kind, CancellationToken external)
    {
        _passCts?.Cancel();
        _passCts?.Dispose();
        _passCts = CancellationTokenSource.CreateLinkedTokenSource(external);
        _version++;
        var pass = new PassScope(kind, _version, _passCts.Token);
        _currentPass = pass;
        return pass;
    }

    /// <summary>
    /// Retires the pass in flight: the validating flag, the field scope narrowing it, and the
    /// descriptor naming the pass all clear together, so nothing that runs afterwards can read a
    /// pass that has already ended. The caller establishes that the pass is still the current one —
    /// the verdict dispatch does that with its own version guard, <see cref="SetValidating"/> with
    /// its.
    /// </summary>
    private void EndPass()
    {
        IsValidating = false;
        _validatingScope = null;
        _currentPass = null;
    }

    /// <summary>
    /// Flips <see cref="IsValidating"/> and notifies, marshaled through <see cref="_renderDispatch"/> so
    /// the flip lands on the renderer's dispatcher rather than on whatever thread completed the pass.
    /// The write is skipped when <paramref name="pass"/> is no longer the current one —
    /// a superseded pass must not stomp a newer pass's state. <paramref name="fields"/> narrows which
    /// fields <see cref="GetFieldState"/> reports as validating: a live pass passes the single field
    /// that triggered it; a refresh pass passes the fields edited within its debounce window; a
    /// submit pass passes <see langword="null"/> (form-wide, every field). Only meaningful when
    /// <paramref name="value"/> is <see langword="true"/> — clearing ends the pass outright (see
    /// <see cref="EndPass"/>), scope and descriptor with it.
    /// Also raises the EditContext's own validation-state notification, not just the engine's: a
    /// native InputBase re-renders on that event, not on <see cref="StateChanged"/>, so without it
    /// the Pending class a native input picks up through
    /// <see cref="FormidableFieldCssClassProvider"/> would light on the next store rebuild but have
    /// no later trigger to clear it once the pass ends.
    /// </summary>
    private Task SetValidating(bool value, PassScope pass, HashSet<FieldIdentifier>? fields = null) =>
        _renderDispatch(() =>
        {
            if (pass.Version == _version)
            {
                if (value)
                {
                    IsValidating = true;
                    _validatingScope = fields;
                }
                else
                {
                    EndPass();
                }

                NotifyStateChanged();
                EditContext.NotifyValidationStateChanged();
            }
            return Task.CompletedTask;
        });

    /// <summary>
    /// The one lifecycle every pass runs: begin (taking the version and the linked token that make
    /// the pass superseded-able), validate under <paramref name="profile"/>, dispatch the verdict
    /// only if this pass is still the current one, and end the pass exactly once however it left.
    /// Live, submit and refresh differ in what they hand in, not in how they run — a skeleton
    /// hand-rolled per kind is one where a single copy can quietly stop raising a notification, or
    /// stop clearing a flag, that the other two still do.
    /// </summary>
    /// <param name="kind">Which lifecycle this is; it also decides the fault policy below.</param>
    /// <param name="profile">The profile the model is validated under.</param>
    /// <param name="external">
    /// The caller's own cancellation token, linked into the pass. Only a submit has one; live and
    /// refresh pass <see cref="CancellationToken.None"/>, which is what lets one cancellation filter
    /// serve all three — with no external token there is nothing a cancellation can mean except
    /// supersession by a newer pass.
    /// </param>
    /// <param name="beginScope">
    /// The fields the pending indicator covers, evaluated once the pass has begun: a refresh's scope
    /// is a snapshot-and-clear of an accumulator, so when it is taken is part of what it means.
    /// </param>
    /// <param name="applyVerdict">
    /// Writes this pass's verdict into engine state. Runs on the renderer's dispatcher, only while
    /// the pass is still current, and always in the same dispatch as the store rebuild publishing it.
    /// </param>
    /// <returns>
    /// The report this pass produced, or <see langword="null"/> when it ended before there was one —
    /// superseded mid-validation, or faulted.
    /// </returns>
    private async Task<ValidationReport?> RunPassAsync(
        PassKind kind,
        ValidationProfile profile,
        CancellationToken external,
        Func<HashSet<FieldIdentifier>?> beginScope,
        Action<ValidationReport> applyVerdict)
    {
        var pass = BeginPass(kind, external);
        try
        {
            await SetValidating(true, pass, beginScope()).ConfigureAwait(false);

            ValidationReport report;
            try
            {
                report = await _validator.ValidateAsync(_model, profile, pass.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!external.IsCancellationRequested)
            {
                return null; // superseded by a newer pass, rather than cancelled by the caller
            }
            catch (Exception exception) when (kind != PassKind.Submit)
            {
                // A submit is the one pass someone is awaiting, so a validator that throws under it
                // has somewhere to surface: the caller's own try/catch. A live or refresh pass is
                // fire-and-forget, so its fault has to become form state and an event instead.
                await ReportFaultAsync(pass, exception).ConfigureAwait(false);
                return null;
            }

            await _renderDispatch(() =>
            {
                if (pass.Version != _version)
                {
                    return Task.CompletedTask; // superseded by a newer pass
                }

                _faultIssue = null;
                applyVerdict(report);

                // The pass ends here, not only in the finally below: retiring it before
                // RebuildStore's notification means the verdict and the cleared pending indicator
                // reach every subscriber in one round instead of two back-to-back ones — and the
                // descriptor is gone before any notification, so a handler that reacts by letting a
                // deferred refresh run cannot still see this pass as the one in flight.
                EndPass();

                RebuildStore();
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            return report;
        }
        finally
        {
            // The paths that never reach the verdict dispatch — a superseded pass, a cancelled one,
            // a faulted one — end here instead, and this no-ops once the dispatch has ended the
            // pass itself. It reaches one step further back than a per-kind hand-roll needs to: the
            // start notification is inside the try as well, so a subscriber throwing from there
            // leaves the flag cleared and costs one extra round, rather than leaving it stuck on.
            if (IsValidating)
            {
                await SetValidating(false, pass).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The single fault policy behind every pass that reports rather than rethrows: a form-level
    /// issue saying the verdict is incomplete, written only while <paramref name="pass"/> is still
    /// the current one, then <see cref="ValidationFaulted"/> for a host that wants to log it. The
    /// event is raised either way — a superseded pass's exception still happened.
    /// </summary>
    private async Task ReportFaultAsync(PassScope pass, Exception exception)
    {
        await _renderDispatch(() =>
        {
            if (pass.Version == _version)
            {
                _faultIssue = new ValidationIssue(
                    string.Empty,
                    "Validation could not run to completion; recent changes may not be fully validated.");
                RebuildStore();
            }
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        ValidationFaulted?.Invoke(exception);
    }

    private async Task RunLivePassAsync(FieldIdentifier changedField)
    {
        if (SubmitInFlight)
        {
            return; // submit is the higher-intent operation; live/refresh passes never supersede it
        }

        _pendingLiveFields.Add(changedField);

        await RunPassAsync(
            PassKind.Live,
            _options.LiveProfile,
            CancellationToken.None,
            () => [changedField],
            report =>
            {
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
            }).ConfigureAwait(false);
    }

    private FieldIdentifier Resolve(ValidationIssue issue) =>
        _introspector.Resolve(_model, issue.Path).ToFieldIdentifier(_model, issue.Path);

    /// <summary>
    /// The one report an issue with nowhere to render gets: a Trace line for a debugger, a logged
    /// warning for the host (WebAssembly's default provider is the browser console, so that channel
    /// needs no wiring to be seen), and the options callback for a page that wants to show its own
    /// list. Every site that decides an issue is suppressed ends here, so the three channels can
    /// never drift apart between them.
    /// </summary>
    private void ReportSuppressed(ValidationIssue issue)
    {
        System.Diagnostics.Trace.WriteLine(
            $"Formidable: issue at '{issue.Path}' is suppressed - no rendered field registration matches and no disclosure override applies.");
        _logger?.LogWarning(
            "Formidable: issue at '{Path}' is suppressed - no rendered field registration matches and no disclosure override applies.",
            issue.Path);
        _options.SuppressedIssueDiagnostic?.Invoke(issue);
    }

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
        var canProceed = false;
        var summary = new List<string>();

        var passReport = await RunPassAsync(
            PassKind.Submit,
            _options.SubmitProfile,
            cancellationToken,
            static () => null, // a submit's pending indicator is form-wide: no scope narrows it
            report =>
            {
                HasSubmitted = true;
                _liveIssues.Clear();

                // Submit takes the live channel over wholesale, so a live pass it superseded has
                // nothing left to hand on: its field's verdict is this report's, and any further
                // edit is revalidated by the refresh that edit arms.
                _pendingLiveFields.Clear();

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
                        ReportSuppressed(suppressed.Issue);
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
            }).ConfigureAwait(false);

        // No report at all means the pass was superseded before one existed — report blocked
        // quietly. A pass superseded at the verdict dispatch has a report but never ran the writes
        // above, so the untouched locals are what it has to say: blocked, with nothing to point at.
        return passReport is null
            ? new SubmitOutcome(false, ValidationReport.Empty, [])
            : new SubmitOutcome(canProceed, passReport, summary);
    }

    /// <inheritdoc />
    public void ApplyServerIssues(IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        HasSubmitted = true;
        _faultIssue = null;

        // The payload is the server's CURRENT verdict, not an addition to its last one: undo
        // exactly what the previous call added before applying this call's issues. Each entry is
        // undone from the channel its own severity names — the same one the apply below put it in.
        // ValidationIssue is a record (value equality), so List<T>.Remove takes out one value-equal
        // entry — the instance the previous apply added, or (see RunRefreshPassAsync) the
        // message-matched refreshed issue standing in for it if a refresh landed since. If a
        // client-sourced issue happens to be value-identical to a previously-applied server issue,
        // removing either of the two equal entries is indistinguishable and acceptable.
        foreach (var (field, issue) in _appliedServerIssues)
        {
            var channel = ChannelFor(issue);
            if (channel.TryGetValue(field, out var tracked))
            {
                tracked.Remove(issue);
                if (tracked.Count == 0)
                {
                    channel.Remove(field);
                }
            }
        }

        _appliedServerIssues = [];

        foreach (var issue in issues)
        {
            var field = Resolve(issue);

            if (issue.Severity == ValidationSeverity.Error)
            {
                // Server-declared errors bypass the registry rather than defer to it: the server
                // judged what was actually submitted, and a verdict that blocks the save has to
                // reach the user whether or not the client happened to render the field. Only an
                // explicit override hides one.
                if (_options.DisclosureOverride?.Invoke(issue) == false)
                {
                    continue;
                }
            }
            else if (!IsVisible(issue, field))
            {
                // An advisory blocks nothing, so it follows the same disclosure rule the client's
                // own advisories follow rather than the bypass above. Nowhere to render it means it
                // is not shown, and the diagnostic names the registration that would have shown it.
                // No defensive gate stands in for it either — that gate exists because a hidden
                // error would otherwise fail a submit silently, and an advisory fails nothing.
                ReportSuppressed(issue);
                continue;
            }

            var channel = ChannelFor(issue);
            if (channel.TryGetValue(field, out var existing))
            {
                existing.Add(issue);
            }
            else
            {
                channel[field] = [issue];
            }

            // Sticky: reveal state never un-reveals a field. The two sets are separate because the
            // post-submit refresh watches them separately — a field can be an advisory site without
            // ever having been an error site, and keeps its advisory refreshed either way.
            var revealed = issue.Severity == ValidationSeverity.Error ? _submitVisible : _advisoryVisible;
            revealed.Add(field);

            _appliedServerIssues.Add((field, issue));
        }

        RebuildStore();
    }

    /// <summary>
    /// The submit-time channel an issue belongs to. Errors and advisories are kept apart because a
    /// field can carry both at once and only the errors reach the message store, so a server issue
    /// is applied to, undone from, and matched within the channel its own severity names.
    /// </summary>
    private Dictionary<FieldIdentifier, List<ValidationIssue>> ChannelFor(ValidationIssue issue) =>
        issue.Severity == ValidationSeverity.Error ? _submitIssues : _submitAdvisories;

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
        if (SubmitInFlight || LiveInFlight)
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

        await RunPassAsync(
            PassKind.Refresh,
            _options.SubmitProfile,
            CancellationToken.None,
            () =>
            {
                // Snapshot-and-clear: this window's refresh flags exactly the fields the user
                // edited since the last refresh (or since submit, for the first one). Fields
                // edited while this pass is in flight land in the now-empty accumulator and are
                // flagged by the NEXT refresh instead — they are not lost, just deferred one
                // window (see ScheduleRefresh's re-arm on the deferral branch above for the
                // analogous case). If THIS pass is itself superseded before finishing (a live
                // pass never defers to a refresh — see BeginPass), its already-captured scope is
                // deliberately dropped, not merged into whatever runs next: the superseding pass
                // owns the indicator outright, exactly as one live pass already displaces
                // another's scope pre-submit — this is the same "post-submit editing reads like
                // pre-submit editing" symmetry, not a gap.

                // The empty case is reachable, not a bug: a re-armed timer (see the deferral
                // above) can fire after an earlier refresh pass already snapshotted the union,
                // leaving nothing new accumulated. The ternary's null branch then falls back to
                // form-wide validating for this redundant pass — a brief conservative flash, the
                // pre-scoping behaviour, never a stuck flag.
                var edited = _pendingRefreshFields.Count > 0
                    ? new HashSet<FieldIdentifier>(_pendingRefreshFields)
                    : null;
                _pendingRefreshFields.Clear();
                return edited;
            },
            report =>
            {
                // The previous ApplyServerIssues call's bookkeeping, captured before the refresh's
                // own issues (below) replace both submit channels wholesale.
                var previouslyApplied = _appliedServerIssues;

                // Resurface only what the user already saw at submit AND is still failing —
                // fixed fields clear; fields revealed after submit stay quiet until the next submit.
                _submitIssues = report.Errors
                    .Select(issue => (Issue: issue, Field: Resolve(issue)))
                    .Where(x => _submitVisible.Contains(x.Field))
                    .GroupBy(x => x.Field, x => x.Issue)
                    .ToDictionary(g => g.Key, g => g.ToList());

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

                // Re-key by MESSAGE within the entry's own severity channel, not by field: a field
                // can carry both a server-applied issue and an unrelated, independently-failing
                // client-sourced one at once (see ApplyServerIssues' own remarks), and adopting
                // everything the refresh wrote for a previously-applied field would sweep up that
                // unrelated client issue too, so the next apply would delete it — a worse bug than
                // the duplicate this guards against. Instead, for each issue the previous apply is
                // responsible for, adopt the refreshed issue on that field whose message and
                // severity match it (the duplicate this fixes is by definition an identical
                // message at an identical severity, so this still finds and replaces it) and drop
                // the bookkeeping entry when no refreshed issue matches — the server's contribution
                // is no longer part of what's shown, so there is nothing left to protect. Both
                // channels are rebuilt above before any of this runs, so an entry is matched
                // against the refreshed list it would actually have to stand in for.
                _appliedServerIssues = previouslyApplied
                    .Select(entry => (
                        entry.Field,
                        Issue: ChannelFor(entry.Issue).TryGetValue(entry.Field, out var current)
                            ? current.FirstOrDefault(i =>
                                i.Message == entry.Issue.Message && i.Severity == entry.Issue.Severity)
                            : null))
                    .Where(x => x.Issue is not null)
                    .Select(x => (x.Field, Issue: x.Issue!))
                    .ToList();
            }).ConfigureAwait(false);
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

/// <summary>
/// Which of the engine's three lifecycles a pass is running. The kind is what the engine's own
/// deference rules are written in — a refresh stands down for a live pass and for a submit, a live
/// pass stands down for a submit — and it is also what decides whether a validator's exception is
/// reported as form state or left to the caller awaiting the pass.
/// </summary>
internal enum PassKind
{
    /// <summary>One field's edit, revalidating the model under the live profile.</summary>
    Live,

    /// <summary>The form-wide pass a submit runs, under the submit profile.</summary>
    Submit,

    /// <summary>The debounced post-submit revalidation, also under the submit profile.</summary>
    Refresh,
}

/// <summary>
/// The identity a running pass carries: what it is, the version that decides whether it is still
/// the current pass, and the token it was started with. A pass is superseded — never waited for —
/// so every write it makes has to be gated on the version still being the engine's own.
/// </summary>
/// <param name="Kind">Which lifecycle this pass is running.</param>
/// <param name="Version">The engine version this pass took when it began.</param>
/// <param name="Token">The pass's cancellation token, linked to whatever the caller supplied.</param>
internal readonly record struct PassScope(PassKind Kind, int Version, CancellationToken Token);
