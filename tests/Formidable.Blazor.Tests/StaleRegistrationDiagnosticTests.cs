using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins <see cref="FormidableOptions.ReportStaleRegistrations"/>, the report-never-throw sibling
/// of <see cref="FormidableOptions.VerifyRowKeys"/>: with it on, a bound component whose accessor
/// no longer names the field it registered reports the divergence — a logged warning plus the
/// <see cref="FormidableOptions.StaleRegistrationDiagnostic"/> callback — instead of throwing.
/// The scenario is <c>OwnerReplacementNotifyOrderTests</c>' nested-owner shape: a
/// <see cref="FormidableField{TValue}"/> keyed by the owner wrapping a
/// <see cref="FormidableFieldMessage{TValue}"/>, both with accessors navigating to the owner
/// through the stable model, so replacing the owner re-points the retained message's accessor
/// without any rebuild. Each notify's synchronous publish re-renders the wrapper, which
/// re-parameterizes the retained message — the parameter set the check runs on. The latch
/// contract pinned here: one divergence is one report, a heal (the accessor naming the
/// registered field again) re-arms it, and a rebind starts fresh.
/// </summary>
public class StaleRegistrationDiagnosticTests : BunitContext
{
    private readonly CapturingLoggerProvider _loggerProvider = new();

    public StaleRegistrationDiagnosticTests()
    {
        Services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddProvider(_loggerProvider)));
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, DeclarationOrderValidator>();
    }

    private List<(LogLevel Level, string Message)> StaleWarnings =>
        [.. _loggerProvider.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("registered the field", StringComparison.Ordinal))];

    [Fact]
    public async Task A_divergence_with_the_check_off_reports_once_and_never_throws()
    {
        var reports = new List<StaleRegistrationReport>();
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "original" } };
        var original = order.Customer;
        var cut = Render<NestedOwnerHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Options, new FormidableOptions
            {
                ReportStaleRegistrations = true,
                StaleRegistrationDiagnostic = reports.Add,
            }));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        await cut.InvokeAsync(() =>
        {
            order.Customer = new EngineCustomer();
            engine.EditContext.NotifyFieldChanged(FieldIdentifier.Create(() => order.Customer!.Name));
        });

        cut.WaitForAssertion(() => Assert.Single(reports));
        Assert.False(Renderer.UnhandledException.IsCompleted);

        // The one notify already drove several publishes (the touched flip, the live pass), each
        // re-parameterizing the retained message under the standing divergence — so "once" is
        // already the latch speaking. A second notify drives more of them; still one report.
        await cut.InvokeAsync(() =>
            engine.EditContext.NotifyFieldChanged(new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name))));

        var report = Assert.Single(reports);
        Assert.Equal(typeof(FormidableFieldMessage<string>), report.ComponentType);
        Assert.Equal(new FieldIdentifier(original, nameof(EngineCustomer.Name)), report.RegisteredField);
        Assert.Equal(new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name)), report.CurrentField);

        var warning = Assert.Single(StaleWarnings);
        Assert.Contains("FormidableFieldMessage", warning.Message, StringComparison.Ordinal);
        Assert.Contains("same field on a different EngineCustomer", warning.Message, StringComparison.Ordinal);
        Assert.Contains("@key", warning.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(FormidableOptions.ReportStaleRegistrations), warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_healed_then_reopened_divergence_is_a_fresh_finding()
    {
        var reports = new List<StaleRegistrationReport>();
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "original" } };
        var original = order.Customer;
        var cut = Render<NestedOwnerHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Options, new FormidableOptions
            {
                ReportStaleRegistrations = true,
                StaleRegistrationDiagnostic = reports.Add,
            }));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        // Diverge: the retained message's accessor resolves the replacement while its
        // registration still names the original.
        var replacement = new EngineCustomer();
        await cut.InvokeAsync(() =>
        {
            order.Customer = replacement;
            engine.EditContext.NotifyFieldChanged(new FieldIdentifier(replacement, nameof(EngineCustomer.Name)));
        });
        cut.WaitForAssertion(() => Assert.Single(reports));

        // Heal: swap the original back. The parameter set the notify drives finds the accessor
        // naming the registered field again, which re-arms the latch.
        await cut.InvokeAsync(() =>
        {
            order.Customer = original;
            engine.EditContext.NotifyFieldChanged(new FieldIdentifier(original, nameof(EngineCustomer.Name)));
        });
        Assert.Single(reports);

        // Reopen with a third instance: a fresh finding, reported again.
        var third = new EngineCustomer();
        await cut.InvokeAsync(() =>
        {
            order.Customer = third;
            engine.EditContext.NotifyFieldChanged(new FieldIdentifier(third, nameof(EngineCustomer.Name)));
        });

        cut.WaitForAssertion(() => Assert.Equal(2, reports.Count));
        Assert.Equal(new FieldIdentifier(original, nameof(EngineCustomer.Name)), reports[1].RegisteredField);
        Assert.Equal(new FieldIdentifier(third, nameof(EngineCustomer.Name)), reports[1].CurrentField);
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    [Fact]
    public async Task VerifyRowKeys_still_throws_and_the_report_channels_stay_silent()
    {
        var reports = new List<StaleRegistrationReport>();
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "original" } };
        var cut = Render<NestedOwnerHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Options, new FormidableOptions
            {
                VerifyRowKeys = true,
                ReportStaleRegistrations = true,
                StaleRegistrationDiagnostic = reports.Add,
            }));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        await cut.InvokeAsync(() =>
        {
            order.Customer = new EngineCustomer();
            engine.EditContext.NotifyFieldChanged(FieldIdentifier.Create(() => order.Customer!.Name));
        });

        // The throw is the one the option has always raised, surfacing on the renderer's
        // unhandled-exception path — and it REPLACES the report: one divergence is not two
        // findings, so neither the callback nor the warning channel sees anything.
        var thrown = await Renderer.UnhandledException.WaitAsync(TimeSpan.FromSeconds(5));
        var invalid = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains(
            $"{nameof(FormidableOptions)}.{nameof(FormidableOptions.VerifyRowKeys)}",
            invalid.Message,
            StringComparison.Ordinal);
        Assert.Contains("same field on a different EngineCustomer", invalid.Message, StringComparison.Ordinal);
        Assert.Empty(reports);
        Assert.Empty(StaleWarnings);
    }

    [Fact]
    public async Task Nothing_diverging_reports_nothing()
    {
        var reports = new List<StaleRegistrationReport>();
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "original" } };
        var cut = Render<NestedOwnerHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Options, new FormidableOptions
            {
                ReportStaleRegistrations = true,
                StaleRegistrationDiagnostic = reports.Add,
            }));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        // The documented safe order: replace, render (the keyed diff rebuilds the bound
        // components against the replacement), then notify — plus steady-state re-renders,
        // which run the check on every bound component and find nothing.
        order.Customer = new EngineCustomer();
        cut.Render(parameters => parameters.Add(p => p.Order, order));
        await cut.InvokeAsync(() =>
            engine.EditContext.NotifyFieldChanged(FieldIdentifier.Create(() => order.Customer!.Name)));
        cut.Render(parameters => parameters.Add(p => p.Order, order));
        cut.Render(parameters => parameters.Add(p => p.Order, order));

        Assert.Empty(reports);
        Assert.Empty(StaleWarnings);
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    [Fact]
    public async Task Detection_is_off_by_default_even_with_the_callback_set()
    {
        var reports = new List<StaleRegistrationReport>();
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "original" } };
        var cut = Render<NestedOwnerHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Options, new FormidableOptions
            {
                StaleRegistrationDiagnostic = reports.Add,
            }));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        await cut.InvokeAsync(() =>
        {
            order.Customer = new EngineCustomer();
            engine.EditContext.NotifyFieldChanged(FieldIdentifier.Create(() => order.Customer!.Name));
        });
        await cut.InvokeAsync(() =>
            engine.EditContext.NotifyFieldChanged(new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name))));

        // ReportStaleRegistrations is the switch, and it defaults to off: setting the callback
        // alone detects nothing, exactly as the option's entry documents.
        Assert.Empty(reports);
        Assert.Empty(StaleWarnings);
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }

    /// <summary>
    /// <c>OwnerReplacementNotifyOrderTests</c>' markup shape: a <see cref="FormidableField{TValue}"/>
    /// keyed by the owner instance, wrapping a <see cref="FormidableFieldMessage{TValue}"/>, both
    /// with accessors that navigate to the owner through the stable model
    /// (<c>Order.Customer!.Name</c>) rather than closing over the instance itself — which is what
    /// lets a replacement re-point them without any rebuild.
    /// </summary>
    private sealed class NestedOwnerHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public FormidableOptions? Options { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableField<string>>(10);
                inner.SetKey(Order.Customer);
                inner.AddComponentParameter(11, nameof(FormidableField<string>.For), (Expression<Func<string>>)(() => Order.Customer!.Name));
                inner.AddComponentParameter(12, nameof(FormidableField<string>.ChildContent), (RenderFragment<FormidableFieldContext>)(_ => content =>
                {
                    content.OpenComponent<FormidableFieldMessage<string>>(0);
                    content.AddComponentParameter(1, nameof(FormidableFieldMessage<string>.For), (Expression<Func<string>>)(() => Order.Customer!.Name));
                    content.CloseComponent();
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
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
