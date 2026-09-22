using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

// Adaptations (see task report):
// 1. bunit 2.9.0's matcher-based SetupVoid overload returns a handler that must be explicitly
//    completed via SetVoidResult() (identifier+args overloads auto-complete; the
//    InvocationMatcher overload used here does not) — same discovery as FocusServiceTests.cs.
// 2. FormSummary injects IFormidableFocusService (IAsyncDisposable-only, per the binding
//    contract), so rendering it here — same as FocusServiceTests.cs resolving the service
//    directly — makes the DI container capture a FormidableFocusService for disposal.
//    BunitContext's xUnit teardown calls the synchronous IDisposable.Dispose(), which throws
//    for an object that only implements IAsyncDisposable. Each test is async and explicitly
//    awaits Services.DisposeAsync() so the container is already disposed (idempotent) by the
//    time xUnit's synchronous teardown runs. Making the test methods async (for that trailing
//    await) turns the existing fire-and-forget `form.InvokeAsync(() => ...SubmitAsync())` calls
//    (unawaited by design — WaitForAssertion below polls for the eventual render) into CS4014
//    errors under TreatWarningsAsErrors, so they are explicitly discarded with `_ = `.
public class FormSummaryTests : BunitContext
{
    public FormSummaryTests()
    {
        Services.AddFormidableBlazor();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js").SetupVoid("focusField", _ => true).SetVoidResult();
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderWithSummary(EngineOrder order)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "Options", new FormidableOptions { DisclosureOverride = _ => true });
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormSummary>(0);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<EngineOrder>>();
    }

    [Fact]
    public async Task Renders_nothing_when_clean()
    {
        var form = RenderWithSummary(new EngineOrder { Description = "ok", Customer = new EngineCustomer() });

        Assert.Empty(form.FindAll(".formidable-summary"));

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task Groups_by_severity_with_errors_first()
    {
        var order = new EngineOrder { Description = "a-b", Customer = null }; // error (Customer) + warning (hyphens)
        var form = RenderWithSummary(order);

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            var groups = form.FindAll("ul.formidable-summary__group");
            Assert.Equal(2, groups.Count);
            Assert.Contains("--error", groups[0].GetAttribute("class"));
            Assert.Contains("--warning", groups[1].GetAttribute("class"));
            Assert.Equal("alert", form.Find(".formidable-summary").GetAttribute("role"));
        });

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task Click_focuses_the_field_via_js()
    {
        var order = new EngineOrder();
        var form = RenderWithSummary(order);
        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.NotEmpty(form.FindAll("button.formidable-summary__link")));

        form.FindAll("button.formidable-summary__link")[0].Click();

        JSInterop.VerifyInvoke("focusField");

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task Summary_updates_on_state_changes()
    {
        var order = new EngineOrder();
        var form = RenderWithSummary(order);
        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.NotEmpty(form.FindAll(".formidable-summary")));

        order.Description = "ok";
        order.Customer = new EngineCustomer();
        _ = form.InvokeAsync(() => form.Instance.SubmitAsync()); // valid submit clears everything

        form.WaitForAssertion(() => Assert.Empty(form.FindAll(".formidable-summary")));

        await Services.DisposeAsync();
    }
}
