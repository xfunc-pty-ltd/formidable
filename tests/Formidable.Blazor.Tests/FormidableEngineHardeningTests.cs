using System.Diagnostics;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormidableEngineHardeningTests
{
    [Fact]
    public async Task Edit_during_first_submit_gets_revalidated_after_submit_completes()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, time);

        var submit = engine.ValidateForSubmitAsync();
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        time.Advance(TimeSpan.FromMilliseconds(301)); // debounce elapses while submit is in flight -> defer + re-arm

        validator.Gate.SetResult();
        await submit;
        Assert.Equal(1, validator.Started);

        time.Advance(TimeSpan.FromMilliseconds(301)); // the re-armed refresh now runs
        Assert.Equal(2, validator.Started);
    }

    // Pins the post-submit lifetime of advisories (warnings/info). At submit the engine captures
    // non-error issues for every VISIBLE field, which includes fields carrying no error at all.
    // The debounced refresh must keep those fields' advisories current - an advisory view read
    // through the error-reveal ledger alone would silently drop a warning shown on an error-free
    // field the moment any other field changed.
    [Fact]
    public async Task Submit_warning_on_an_error_free_field_survives_the_debounced_refresh()
    {
        var order = new EngineOrder { Description = "a-b" }; // NotEmpty passes; the hyphen warning fires
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, time);

        // Customer is null, so submit records an error elsewhere and HasSubmitted stays true.
        await engine.ValidateForSubmitAsync();
        Assert.Contains(engine.GetIssues(description), i => i.Message == "Avoid hyphens");

        order.Customer = new EngineCustomer();
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Customer)));
        time.Advance(TimeSpan.FromMilliseconds(301)); // the debounced refresh pass runs
        await Task.Yield();

        Assert.Contains(engine.GetIssues(description), i => i.Message == "Avoid hyphens");
    }

    // Same lifetime pin as above, but with NO co-occurring error anywhere in the model: the
    // submit report is otherwise valid (report.IsValid == true), so this exercises the
    // valid branch's advisory-ledger re-freeze rather than the invalid branch's union. A valid
    // branch that reset the advisory ledger without re-freezing it to the fresh sites would
    // leave the warning captured at submit but drop it the moment any field changes and the
    // debounced refresh runs, because the advisory view discloses only fields one of the two
    // reveal ledgers watches and the error ledger is empty on an errors-free submit.
    [Fact]
    public async Task Submit_warning_with_no_errors_anywhere_survives_the_debounced_refresh()
    {
        var order = new EngineOrder { Description = "a-b", Customer = new EngineCustomer() }; // NotEmpty and NotNull both pass; only the hyphen warning fires
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, time);

        // No error anywhere in the model, so this submit takes the report.IsValid branch.
        await engine.ValidateForSubmitAsync();
        Assert.Contains(engine.GetIssues(description), i => i.Message == "Avoid hyphens");

        // Nothing to fix here (there was never an error) - just trigger some field change so
        // HasSubmitted schedules the debounced refresh.
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Customer)));
        time.Advance(TimeSpan.FromMilliseconds(301)); // the debounced refresh pass runs
        await Task.Yield();

        Assert.Contains(engine.GetIssues(description), i => i.Message == "Avoid hyphens");
    }

    // RunRefreshPassAsync's own entry guard, pinned the same way its debounced-live-pass sibling
    // already is: a timer fire dispatches RunRefreshPassAsync through _renderDispatch, and that
    // dispatch can still be QUEUED — not yet run — when Dispose() tears the engine down. Without
    // an entry guard, the queued call reaches BeginPass and cancels a _passCts Dispose() already
    // cancelled and disposed, throwing ObjectDisposedException into a discarded task. LiveDebounce
    // is left unset, so the field change below runs its live pass synchronously and fully - before
    // interceptNext ever flips true - leaving the refresh timer's own dispatch as the only thing
    // this test's dispatch override ever gets a chance to intercept.
    [Fact]
    public async Task Refresh_dispatch_still_queued_when_engine_is_disposed_does_not_throw()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var interceptNext = false;
        Func<Task>? queued = null;
        var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            time,
            renderDispatch: work =>
            {
                if (interceptNext)
                {
                    interceptNext = false;
                    queued = work;
                    return Task.CompletedTask;
                }

                return work();
            });

        await engine.ValidateForSubmitAsync(); // report.IsValid -> HasSubmitted, no errors to chase
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));

        interceptNext = true;
        time.Advance(TimeSpan.FromMilliseconds(301)); // arms + fires the refresh timer only
        Assert.NotNull(queued);

        engine.Dispose();

        await queued!(); // the queued dispatch finally runs against the now-disposed engine
    }

    [Fact]
    public async Task ApplyServerIssues_clears_a_prior_fault()
    {
        var order = new EngineOrder();
        var validator = new ThrowingValidator();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(), new FakeTimeProvider());

        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        await Task.Yield();
        Assert.NotEmpty(editContext.GetValidationMessages(new FieldIdentifier(order, string.Empty)));

        engine.ApplyServerIssues([new ValidationIssue("Description", "server said no")]);

        Assert.DoesNotContain(
            editContext.GetValidationMessages(new FieldIdentifier(order, string.Empty)),
            m => m.Contains("could not run to completion"));
        Assert.Contains("server said no",
            editContext.GetValidationMessages(new FieldIdentifier(order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public async Task Suppressed_issues_reach_the_diagnostic_callback()
    {
        var order = new EngineOrder();
        var suppressed = new List<ValidationIssue>();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { SuppressedIssueDiagnostic = suppressed.Add }, new FakeTimeProvider());
        using var reg = engine.Registry.Register(new FieldIdentifier(order, nameof(EngineOrder.Description)));

        await engine.ValidateForSubmitAsync();

        Assert.Contains(suppressed, i => i.Path == nameof(EngineOrder.Customer)); // unregistered -> suppressed
        Assert.DoesNotContain(suppressed, i => i.Path == nameof(EngineOrder.Description)); // registered -> visible
    }

    [Fact]
    public async Task Diagnostic_is_not_invoked_when_override_reveals_everything()
    {
        var order = new EngineOrder();
        var suppressed = new List<ValidationIssue>();
        var editContext = new EditContext(order);
        var customer = new FieldIdentifier(order, nameof(EngineOrder.Customer));
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true, SuppressedIssueDiagnostic = suppressed.Add },
            new FakeTimeProvider());

        await engine.ValidateForSubmitAsync();

        Assert.Empty(suppressed);

        // Customer is registered nowhere, which is the exact issue the sibling above watches the
        // diagnostic report. Its message reaching the store is what says the pass produced that
        // issue and the override revealed it, rather than the pass producing nothing to suppress.
        Assert.NotEmpty(editContext.GetValidationMessages(customer));
    }

    // Both ReportSuppressed (above) and FirstErrorFocus.ReportFallbackMiss echo a
    // ValidationIssue.Path into a Trace line and a formatted log message. That Path is
    // payload-supplied on the ApplyServerIssues route - nothing upstream constrains its shape -
    // so a forged one carrying '\r'/'\n' would split either line, and an unbounded one costs the
    // line whatever length the payload names. DiagnosticPathSanitizer.ForDiagnostic is the shared
    // fix; these four pins cover both call sites for both contracts, so bypassing the sanitizer at
    // either site fails that site's own pair on its own.

    [Fact]
    public void Suppressed_issue_diagnostic_neutralizes_a_forged_newline_in_the_path()
    {
        var order = new EngineOrder();
        var logger = new CapturingLogger();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(), new FakeTimeProvider(), logger: logger);

        // Never registered and Warning severity, so ApplyServerIssues suppresses it - the
        // ReportSuppressed call site this pins.
        const string marker = "forged suppressed-issue log line";
        var forgedPath = $"Description\r\nFormidable: {marker}";
        var traceLines = CaptureTrace(() =>
            engine.ApplyServerIssues(
                [new ValidationIssue(forgedPath, "server said no", ValidationSeverity.Warning)]));

        // Matched by content, not by count: the suite runs other test classes concurrently, and
        // any of them can add its own unrelated Trace.WriteLine call to this same process-wide
        // listener while it is attached - this line is the one this test's own call produced.
        var traceLine = Assert.Single(traceLines, line => line.Contains(marker));
        Assert.DoesNotContain("\r", traceLine);
        Assert.DoesNotContain("\n", traceLine);

        var logged = Assert.Single(logger.Messages);
        Assert.DoesNotContain("\r", logged);
        Assert.DoesNotContain("\n", logged);
    }

    [Fact]
    public void Suppressed_issue_diagnostic_bounds_an_overlong_forged_path()
    {
        var order = new EngineOrder();
        var logger = new CapturingLogger();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(), new FakeTimeProvider(), logger: logger);

        const string marker = "SuppressedForgedBound";
        var forgedPath = marker + new string('x', 5000);
        var traceLines = CaptureTrace(() =>
            engine.ApplyServerIssues(
                [new ValidationIssue(forgedPath, "server said no", ValidationSeverity.Warning)]));

        // Matched by content, not by count - see the sibling CRLF pin above for why.
        var traceLine = Assert.Single(traceLines, line => line.Contains(marker));
        Assert.DoesNotContain(forgedPath, traceLine);
        Assert.Contains("more", traceLine); // the bound is a visible marker, not a silent cut

        var logged = Assert.Single(logger.Messages);
        Assert.DoesNotContain(forgedPath, logged);
        Assert.Contains("more", logged);
    }

    [Fact]
    public async Task Fallback_miss_diagnostic_neutralizes_a_forged_newline_in_the_path()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(), new FakeTimeProvider());

        // Error severity bypasses the registry entirely, so this reaches GetVisibleIssues with no
        // field ever registered - what MoveAsync then picks as the "first error" to focus.
        const string marker = "forged fallback-miss log line";
        var forgedPath = $"Description\r\nFormidable: {marker}";
        engine.ApplyServerIssues([new ValidationIssue(forgedPath, "server said no")]);

        var focus = new RecordingFocusService { Lands = false }; // never takes focus -> the miss
        var logger = new CapturingLogger();
        var services = new FakeFocusServiceProvider(focus, new CapturingLoggerFactory(logger));

        var traceLines = await CaptureTraceAsync(
            () => FirstErrorFocus.MoveAsync(services, engine, fallback: null, prepare: null).AsTask());

        // Matched by content, not by count: the suite runs other test classes concurrently, and
        // any of them can add its own unrelated Trace.WriteLine call to this same process-wide
        // listener while it is attached - this line is the one this test's own call produced.
        var traceLine = Assert.Single(traceLines, line => line.Contains(marker));
        Assert.DoesNotContain("\r", traceLine);
        Assert.DoesNotContain("\n", traceLine);

        var logged = Assert.Single(logger.Messages);
        Assert.DoesNotContain("\r", logged);
        Assert.DoesNotContain("\n", logged);
    }

    [Fact]
    public async Task Fallback_miss_diagnostic_bounds_an_overlong_forged_path()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = new FormidableEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(), new FakeTimeProvider());

        const string marker = "FallbackForgedBound";
        var forgedPath = marker + new string('x', 5000);
        engine.ApplyServerIssues([new ValidationIssue(forgedPath, "server said no")]);

        var focus = new RecordingFocusService { Lands = false };
        var logger = new CapturingLogger();
        var services = new FakeFocusServiceProvider(focus, new CapturingLoggerFactory(logger));

        var traceLines = await CaptureTraceAsync(
            () => FirstErrorFocus.MoveAsync(services, engine, fallback: null, prepare: null).AsTask());

        // Matched by content, not by count - see the sibling CRLF pin above for why.
        var traceLine = Assert.Single(traceLines, line => line.Contains(marker));
        Assert.DoesNotContain(forgedPath, traceLine);
        Assert.Contains("more", traceLine);

        var logged = Assert.Single(logger.Messages);
        Assert.DoesNotContain(forgedPath, logged);
        Assert.Contains("more", logged);
    }

    /// <summary>
    /// Runs <paramref name="act"/> with a listener attached only for its duration, so a forged
    /// Trace.WriteLine call is captured without leaving a listener behind for any other test.
    /// </summary>
    private static List<string> CaptureTrace(Action act)
    {
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            act();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        return listener.Lines;
    }

    /// <summary>Async sibling of <see cref="CaptureTrace"/>, for a call site reached only through
    /// an awaited path.</summary>
    private static async Task<List<string>> CaptureTraceAsync(Func<Task> act)
    {
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            await act();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        return listener.Lines;
    }

    private sealed class CapturingTraceListener : TraceListener
    {
        public List<string> Lines { get; } = [];

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message) => Lines.Add(message ?? string.Empty);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class CapturingLoggerFactory(CapturingLogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Resolves only the two services <c>FirstErrorFocus.MoveAsync</c> reaches for, so the pin
    /// needs no full DI container.
    /// </summary>
    private sealed class FakeFocusServiceProvider(
        IFormidableFocusService focus, ILoggerFactory loggerFactory) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IFormidableFocusService) ? focus :
            serviceType == typeof(ILoggerFactory) ? loggerFactory :
            null;
    }
}
