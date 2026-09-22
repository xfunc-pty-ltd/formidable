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
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(true);
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderWithSummary(
        EngineOrder order,
        Func<FieldIdentifier, ValueTask<bool>>? focusFallback = null,
        SummaryFilter show = SummaryFilter.All,
        string? errorsHeading = null,
        string? warningsHeading = null,
        string? infosHeading = null,
        int? headingLevel = null)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "Options", new FormidableOptions { DisclosureOverride = _ => true });
            // The form's own default auto-focus (FocusFirstErrorOnInvalidSubmit) would add a
            // focusField call on every blocked submit alongside the summary's click-triggered
            // ones this file pins exact counts for; opted out here so those counts stay about the
            // summary alone.
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.FocusFirstErrorOnInvalidSubmit), false);
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                inner.AddComponentParameter(1, "Show", show);
                if (focusFallback is not null)
                {
                    inner.AddComponentParameter(2, "FocusFallback", focusFallback);
                }
                if (errorsHeading is not null)
                {
                    inner.AddComponentParameter(3, nameof(FormidableSummary.ErrorsHeading), errorsHeading);
                }
                if (warningsHeading is not null)
                {
                    inner.AddComponentParameter(4, nameof(FormidableSummary.WarningsHeading), warningsHeading);
                }
                if (infosHeading is not null)
                {
                    inner.AddComponentParameter(5, nameof(FormidableSummary.InfosHeading), infosHeading);
                }
                if (headingLevel is not null)
                {
                    inner.AddComponentParameter(6, nameof(FormidableSummary.HeadingLevel), headingLevel.Value);
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
    public async Task Each_severity_band_is_wrapped()
    {
        var order = new EngineOrder { Description = "a-b", Customer = null }; // error (Customer) + warning (hyphens)
        var form = RenderWithSummary(order);

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            var bands = form.FindAll(".formidable-summary__band");
            Assert.Equal(2, bands.Count);
            foreach (var band in bands)
            {
                Assert.Single(band.QuerySelectorAll("ul.formidable-summary__group"));
            }
        });

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task No_heading_renders_no_heading_element_and_no_aria_labelledby()
    {
        var order = new EngineOrder { Description = "a-b!", Customer = null }; // error + warning + info
        var form = RenderWithSummary(order);

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            Assert.NotEmpty(form.FindAll(".formidable-summary__band"));
            Assert.Empty(form.FindAll(".formidable-summary__heading"));
            Assert.All(
                form.FindAll("ul.formidable-summary__group"),
                group => Assert.Null(group.GetAttribute("aria-labelledby")));
        });

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task A_heading_is_associated_with_its_list()
    {
        var order = new EngineOrder { Description = "a-b", Customer = null }; // error (Customer) + warning (hyphens)
        var form = RenderWithSummary(order, errorsHeading: "Fix these before you can submit");

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            var heading = form.Find(".formidable-summary__heading");
            var errorList = form.Find("ul.formidable-summary__group--error");
            var headingId = heading.GetAttribute("id");

            Assert.False(string.IsNullOrEmpty(headingId));
            Assert.Equal(headingId, errorList.GetAttribute("aria-labelledby"));
        });

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task A_heading_applies_only_to_its_own_band()
    {
        var order = new EngineOrder { Description = "a-b", Customer = null }; // error (Customer) + warning (hyphens)
        var form = RenderWithSummary(order, warningsHeading: "Worth a look");

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            Assert.Single(form.FindAll(".formidable-summary__heading"));

            var warningList = form.Find("ul.formidable-summary__group--warning");
            var errorList = form.Find("ul.formidable-summary__group--error");

            Assert.False(string.IsNullOrEmpty(warningList.GetAttribute("aria-labelledby")));
            Assert.Null(errorList.GetAttribute("aria-labelledby"));
        });

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task A_non_default_heading_level_renders_that_element()
    {
        var order = new EngineOrder { Description = "a-b", Customer = null }; // error (Customer) + warning (hyphens)
        var form = RenderWithSummary(order, errorsHeading: "Fix these first", headingLevel: 3);

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            var heading = form.Find("h3.formidable-summary__heading");
            Assert.Equal("Fix these first", heading.TextContent);
        });

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task An_out_of_range_heading_level_throws()
    {
        var order = new EngineOrder();

        var exception = Assert.ThrowsAny<Exception>(() => RenderWithSummary(order, headingLevel: 0));

        Assert.Contains(nameof(FormidableSummary.HeadingLevel), exception.Message);

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task Advisory_only_visible_issues_announce_politely()
    {
        var order = new EngineOrder { Description = "a-b", Customer = new EngineCustomer() }; // warning (hyphens) only; no errors
        var form = RenderWithSummary(order);

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() => Assert.Equal("status", form.Find(".formidable-summary").GetAttribute("role")));

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task Errors_filter_shows_only_errors_and_announces_assertively()
    {
        var order = new EngineOrder { Description = "a-b", Customer = null }; // error (Customer) + warning (hyphens)
        var form = RenderWithSummary(order, show: SummaryFilter.Errors);

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            var summary = form.Find("div.formidable-summary");
            Assert.Equal("alert", summary.GetAttribute("role"));
            Assert.Single(form.FindAll("ul.formidable-summary__group--error"));
            Assert.Empty(form.FindAll("ul.formidable-summary__group--warning"));
        });

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task Advisories_filter_shows_warnings_and_infos_and_announces_politely()
    {
        // Advisories is the one filter spanning two severities, so the order carries both: a
        // hyphen draws the warning, an exclamation mark the info, and a null customer the error
        // the filter has to leave out.
        var order = new EngineOrder { Description = "a-b!", Customer = null };
        var form = RenderWithSummary(order, show: SummaryFilter.Advisories);

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            var summary = form.Find("div.formidable-summary");
            Assert.Equal("status", summary.GetAttribute("role"));
            Assert.Single(form.FindAll("ul.formidable-summary__group--warning"));
            Assert.Single(form.FindAll("ul.formidable-summary__group--info"));
            Assert.Empty(form.FindAll("ul.formidable-summary__group--error"));
        });

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_renders_nothing()
    {
        var order = new EngineOrder(); // errors only: Description + Customer both NotEmpty/NotNull
        var form = RenderWithSummary(order, show: SummaryFilter.Infos);

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        // The raw (unfiltered) engine state confirms the submit pass landed with visible
        // issues; the assertion below then confirms the Infos filter matched none of them.
        form.WaitForAssertion(() => Assert.NotEmpty(form.Instance.Engine!.GetVisibleIssues()));
        Assert.Empty(form.FindAll("div.formidable-summary"));

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
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
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
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
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
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
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
