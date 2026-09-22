using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

// Adaptations:
// 1. bunit 2.9.0's matcher-based SetupVoid overload returns a handler that must be explicitly
//    completed via SetVoidResult() (identifier+args overloads auto-complete; the
//    InvocationMatcher overload used here does not) — same discovery as FocusServiceTests.cs.
// 2. FormidableSummary injects IFormidableFocusService, so rendering it here — same as
//    FocusServiceTests.cs resolving the service directly — makes the DI container capture a
//    FormidableFocusService for disposal. Each test is async and explicitly awaits
//    Services.DisposeAsync() so the container is disposed (idempotent) through the service's
//    proper async release path before xUnit's synchronous teardown runs — see
//    FocusServiceTests.cs for why that stays the more thorough choice even though
//    FormidableFocusService's own sync Dispose() no longer throws there. Making the test methods
//    async (for that trailing
//    await) turns the existing fire-and-forget `form.InvokeAsync(() => ...SubmitAsync())` calls
//    (unawaited by design — WaitForAssertion below polls for the eventual render) into CS4014
//    errors under TreatWarningsAsErrors, so they are explicitly discarded with `_ = `.
public class FormidableSummaryTests : BunitContext
{
    public FormidableSummaryTests()
    {
        Services.AddFormidableBlazor();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js").Setup<bool>("focusField", _ => true).SetResult(true);
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderWithSummary(
        EngineOrder order,
        Func<FieldIdentifier, ValueTask<bool>>? focusFallback = null)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "Options", new FormidableOptions { DisclosureOverride = _ => true });
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                if (focusFallback is not null)
                {
                    inner.AddComponentParameter(1, "FocusFallback", focusFallback);
                }

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

    [Fact]
    public async Task Fallback_is_invoked_on_focus_miss_and_true_triggers_one_retry()
    {
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(false);
        var fallbackCalls = 0;
        var order = new EngineOrder();
        var form = RenderWithSummary(order, _ =>
        {
            fallbackCalls++;
            return ValueTask.FromResult(true);
        });
        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.NotEmpty(form.FindAll("button.formidable-summary__link")));

        form.FindAll("button.formidable-summary__link")[0].Click();

        Assert.Equal(1, fallbackCalls);
        JSInterop.VerifyInvoke("focusField", 2);

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task Fallback_is_not_invoked_when_focus_succeeds()
    {
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(true);
        var fallbackCalls = 0;
        var order = new EngineOrder();
        var form = RenderWithSummary(order, _ =>
        {
            fallbackCalls++;
            return ValueTask.FromResult(true);
        });
        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.NotEmpty(form.FindAll("button.formidable-summary__link")));

        form.FindAll("button.formidable-summary__link")[0].Click();

        Assert.Equal(0, fallbackCalls);
        JSInterop.VerifyInvoke("focusField", 1);

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task A_false_fallback_result_means_no_retry()
    {
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(false);
        var fallbackCalls = 0;
        var order = new EngineOrder();
        var form = RenderWithSummary(order, _ =>
        {
            fallbackCalls++;
            return ValueTask.FromResult(false);
        });
        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.NotEmpty(form.FindAll("button.formidable-summary__link")));

        form.FindAll("button.formidable-summary__link")[0].Click();

        Assert.Equal(1, fallbackCalls);
        JSInterop.VerifyInvoke("focusField", 1);

        await Services.DisposeAsync();
    }
}
