using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Formidable.Blazor.Tests;

// On top of the pre-existing Trace.WriteLine and SuppressedIssueDiagnostic callback (left
// unchanged - see FormValidationEngineHardeningTests.cs for their own coverage), the suppression
// site also logs through ILogger, so WASM's default browser-console provider shows it with zero
// consumer wiring. This exercises the wiring end to end - through FormidableEngineFactory's
// optional ILoggerFactory resolution from the same IServiceProvider FormidableForm already
// injects - rather than just the engine's own call site.
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
