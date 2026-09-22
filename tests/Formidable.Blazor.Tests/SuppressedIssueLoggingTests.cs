using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

// On top of the pre-existing Trace.WriteLine and SuppressedIssueDiagnostic callback (left
// unchanged - see FormValidationEngineHardeningTests.cs for their own coverage), the suppression
// site also logs through ILogger, so WASM's default browser-console provider shows it with zero
// consumer wiring. This exercises the wiring end to end - through FormidableEngineFactory's
// optional ILoggerFactory resolution from the same IServiceProvider FormidableForm already
// injects - rather than just the engine's own call site. The two NeverRegisteredFieldDiagnostic
// tests below construct the engine directly instead, mirroring FormValidationEngineHardeningTests'
// shape rather than this file's own DI-through-FormidableForm one - they pin the never-registered
// vs registered-then-unregistered distinction, not the logging wiring.
public class SuppressedIssueLoggingTests : BunitContext
{
    [Fact]
    public void Suppressed_issue_at_submit_logs_a_warning()
    {
        var loggerProvider = new CapturingLoggerProvider();
        Services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddProvider(loggerProvider)));
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();

        // Nothing renders a field for Description or Customer, so both submit-rule failures are
        // suppressed - no DisclosureOverride, no registered field for either.
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();

        _ = cut.InvokeAsync(() => form.Instance.SubmitAsync());

        cut.WaitForAssertion(() => Assert.Contains(
            loggerProvider.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("is suppressed")
                && entry.Message.Contains(nameof(EngineOrder.Description))));
    }

    [Fact]
    public async Task No_ILoggerFactory_registered_does_not_throw()
    {
        // ILoggerFactory resolution is optional: a consumer who never registered logging (or a
        // direct FormValidationEngine construction with logger omitted) must submit exactly as
        // before - this is the null-safety half of the logging wiring, not just the happy path.
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();

        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();

        var exception = await Record.ExceptionAsync(() => cut.InvokeAsync(() => form.Instance.SubmitAsync()));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Suppressed_issue_at_a_never_registered_field_also_reaches_the_never_registered_diagnostic()
    {
        var order = new EngineOrder();
        var suppressed = new List<ValidationIssue>();
        var neverRegistered = new List<ValidationIssue>();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                SuppressedIssueDiagnostic = suppressed.Add,
                NeverRegisteredFieldDiagnostic = neverRegistered.Add,
            },
            new FakeTimeProvider());
        using var reg = engine.Registry.Register(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        // Customer is never registered at all - this is Pattern 2, the miswiring the new callback targets.

        await engine.ValidateForSubmitAsync();

        Assert.Contains(suppressed, i => i.Path == nameof(EngineOrder.Customer));
        Assert.Contains(neverRegistered, i => i.Path == nameof(EngineOrder.Customer));
    }

    [Fact]
    public async Task Suppressed_issue_at_a_registered_then_unregistered_field_does_not_reach_the_never_registered_diagnostic()
    {
        var order = new EngineOrder();
        var suppressed = new List<ValidationIssue>();
        var neverRegistered = new List<ValidationIssue>();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                SuppressedIssueDiagnostic = suppressed.Add,
                NeverRegisteredFieldDiagnostic = neverRegistered.Add,
            },
            new FakeTimeProvider());
        using var descReg = engine.Registry.Register(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        // Customer is registered, then unregistered - this is Pattern 1, which stays on the general channel.
        engine.Registry.Register(new FieldIdentifier(order, nameof(EngineOrder.Customer))).Dispose();

        await engine.ValidateForSubmitAsync();

        Assert.Contains(suppressed, i => i.Path == nameof(EngineOrder.Customer));
        Assert.DoesNotContain(neverRegistered, i => i.Path == nameof(EngineOrder.Customer));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
