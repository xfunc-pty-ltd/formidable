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
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
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
    public async Task A_clean_form_renders_the_wrapper_and_both_regions_empty()
    {
        var form = RenderWithSummary(new EngineOrder { Description = "ok", Customer = new EngineCustomer() });

        Assert.Single(form.FindAll("div.formidable-summary"));
        var errorsRegion = form.Find(".formidable-summary__region--errors");
        var advisoriesRegion = form.Find(".formidable-summary__region--advisories");
        Assert.Equal("alert", errorsRegion.GetAttribute("role"));
        Assert.Equal("status", advisoriesRegion.GetAttribute("role"));
        Assert.Empty(form.FindAll(".formidable-summary__band"));

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task Roles_never_change_a_blocked_submit_fills_the_regions_that_already_carry_them()
    {
        var order = new EngineOrder { Description = "a-b", Customer = null }; // error (Customer) + warning (hyphens)
        var form = RenderWithSummary(order);

        // Both fixed-role regions are in the markup before any issue exists — the live regions
        // assistive technology will announce into already carry their roles here.
        Assert.Equal("alert", form.Find(".formidable-summary__region--errors").GetAttribute("role"));
        Assert.Equal("status", form.Find(".formidable-summary__region--advisories").GetAttribute("role"));

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            var errorsRegion = form.Find(".formidable-summary__region--errors");
            var advisoriesRegion = form.Find(".formidable-summary__region--advisories");

            // The bands landed INSIDE the regions whose roles predate them, and neither role moved.
            Assert.Single(errorsRegion.QuerySelectorAll(".formidable-summary__band--error"));
            Assert.Single(advisoriesRegion.QuerySelectorAll(".formidable-summary__band--warning"));
            Assert.Equal("alert", errorsRegion.GetAttribute("role"));
            Assert.Equal("status", advisoriesRegion.GetAttribute("role"));
        });

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
            Assert.Equal("alert", form.Find(".formidable-summary__region--errors").GetAttribute("role"));
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
    public async Task Advisory_only_visible_issues_fill_the_status_region_and_leave_the_alert_region_empty()
    {
        var order = new EngineOrder { Description = "a-b", Customer = new EngineCustomer() }; // warning (hyphens) only; no errors
        var form = RenderWithSummary(order);

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            var advisoriesRegion = form.Find(".formidable-summary__region--advisories");
            Assert.Single(advisoriesRegion.QuerySelectorAll(".formidable-summary__band--warning"));
            Assert.Empty(form.Find(".formidable-summary__region--errors").QuerySelectorAll(".formidable-summary__band"));
        });

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task Errors_filter_renders_only_the_alert_region()
    {
        var order = new EngineOrder { Description = "a-b", Customer = null }; // error (Customer) + warning (hyphens)
        var form = RenderWithSummary(order, show: SummaryFilter.Errors);

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            var errorsRegion = form.Find(".formidable-summary__region--errors");
            Assert.Equal("alert", errorsRegion.GetAttribute("role"));
            Assert.Empty(form.FindAll(".formidable-summary__region--advisories"));
            Assert.Single(form.FindAll("ul.formidable-summary__group--error"));
            Assert.Empty(form.FindAll("ul.formidable-summary__group--warning"));
        });

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task Advisories_filter_renders_only_the_status_region()
    {
        // Advisories is the one filter spanning two severities, so the order carries both: a
        // hyphen draws the warning, an exclamation mark the info, and a null customer the error
        // the filter has to leave out.
        var order = new EngineOrder { Description = "a-b!", Customer = null };
        var form = RenderWithSummary(order, show: SummaryFilter.Advisories);

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            var advisoriesRegion = form.Find(".formidable-summary__region--advisories");
            Assert.Equal("status", advisoriesRegion.GetAttribute("role"));
            Assert.Empty(form.FindAll(".formidable-summary__region--errors"));
            Assert.Single(form.FindAll("ul.formidable-summary__group--warning"));
            Assert.Single(form.FindAll("ul.formidable-summary__group--info"));
            Assert.Empty(form.FindAll("ul.formidable-summary__group--error"));
        });

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_renders_its_region_empty()
    {
        var order = new EngineOrder(); // errors only: Description + Customer both NotEmpty/NotNull
        var form = RenderWithSummary(order, show: SummaryFilter.Infos);

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        // The raw (unfiltered) engine state confirms the submit pass landed with visible
        // issues; the assertions below then confirm the Infos filter matched none of them —
        // and that the region stands empty rather than disappearing.
        form.WaitForAssertion(() => Assert.NotEmpty(form.Instance.Engine!.GetVisibleIssues()));
        Assert.Single(form.FindAll("div.formidable-summary"));
        Assert.Empty(form.Find(".formidable-summary__region--advisories").QuerySelectorAll(".formidable-summary__band"));
        Assert.Empty(form.FindAll(".formidable-summary__region--errors"));

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task A_splatted_class_merges_on_the_wrapper()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                inner.AddComponentParameter(1, "class", "custom-summary");
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        // Exact equality pins the merge policy: the consumer's class first, the structural class
        // appended after it, neither replacing the other.
        Assert.Equal(
            "custom-summary formidable-summary",
            cut.Find("div.formidable-summary").GetAttribute("class"));

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task The_splat_reaches_the_wrapper_and_never_the_regions()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                inner.AddComponentParameter(1, "data-hook", "mine");
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

        // The splat lands on the wrapper alone; the fixed-role regions are contract, so a
        // consumer cannot decorate them — or re-role one — by splatting.
        Assert.Equal("mine", cut.Find("div.formidable-summary").GetAttribute("data-hook"));
        var errorsRegion = cut.Find(".formidable-summary__region--errors");
        var advisoriesRegion = cut.Find(".formidable-summary__region--advisories");
        Assert.Null(errorsRegion.GetAttribute("data-hook"));
        Assert.Null(advisoriesRegion.GetAttribute("data-hook"));
        Assert.Equal("alert", errorsRegion.GetAttribute("role"));
        Assert.Equal("status", advisoriesRegion.GetAttribute("role"));

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

        form.WaitForAssertion(() => JSInterop.VerifyInvoke("focusField"));

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task Summary_updates_on_state_changes()
    {
        var order = new EngineOrder();
        var form = RenderWithSummary(order);
        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.NotEmpty(form.FindAll(".formidable-summary__band")));

        order.Description = "ok";
        order.Customer = new EngineCustomer();
        _ = form.InvokeAsync(() => form.Instance.SubmitAsync()); // valid submit clears everything

        // The bands go; the wrapper and its regions stay, so the clearing happens inside the
        // same live regions the issues were announced from.
        form.WaitForAssertion(() => Assert.Empty(form.FindAll(".formidable-summary__band")));
        Assert.Single(form.FindAll(".formidable-summary"));

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

        form.WaitForAssertion(() => Assert.Equal(1, fallbackCalls));

        // A true fallback answer only ARMS the retry (TryFocusAsync awaits the fallback, then
        // calls focusField again after it resolves), so fallbackCalls reaching 1 proves only the
        // first focusField call happened, not the retry that follows it.
        form.WaitForAssertion(() => JSInterop.VerifyInvoke("focusField", 2));

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

        form.WaitForAssertion(() => Assert.Equal(0, fallbackCalls));

        // fallbackCalls stays 0 for the whole test (a focus hit never calls the fallback at
        // all), so the wait above is satisfied at once and proves nothing about the click; the
        // focusField count still needs its own wait.
        form.WaitForAssertion(() => JSInterop.VerifyInvoke("focusField", 1));

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

        form.WaitForAssertion(() => Assert.Equal(1, fallbackCalls));

        // A false fallback answer means TryFocusAsync returns without a second focusField call,
        // so the wait above, having already observed the fallback that follows the one call,
        // stands as proof the count is settled at 1 for good.
        JSInterop.VerifyInvoke("focusField", 1);

        await Services.DisposeAsync();
    }
}
