using System.Text.RegularExpressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// What a summary entry says and what clicking it does: <c>ItemTemplate</c>, <c>GroupByField</c>,
/// <c>MaxItems</c>, <c>OverflowTemplate</c> and <c>OnItemActivated</c>. Every one of them is
/// additive, so the first test here is the one that matters most — the whole rendering with none
/// of them set, pinned against the markup as it stands rather than against anybody's
/// reconstruction of it.
/// </summary>
/// <remarks>
/// <see cref="TwiceFailingFieldValidator"/> is what makes the counting questions answerable: it
/// fails <c>Description</c> twice at error severity and once at each advisory severity, so the
/// error band holds three issues across two fields and the same field appears in all three bands.
/// Its two error rules also carry DIFFERENT display names ("Order description" from a
/// <c>WithName</c>, "Description" derived from the path), which is what separates grouping by
/// field from grouping by name: only the first collapses them.
/// </remarks>
public class FormidableSummaryDisplayTests : BunitContext
{
    private readonly RecordingFocusService _focus = new();

    public FormidableSummaryDisplayTests()
    {
        Services.AddSingleton<IFormidableFocusService>(_focus);
        Services.AddFormidableBlazor();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, TwiceFailingFieldValidator>();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
    }

    /// <summary>
    /// Two values in the rendered markup belong to the renderer rather than to this component: the
    /// owner-instance hash inside a minted heading id (a runtime object hash, different every
    /// process) and the integer the renderer assigns each event handler. Both are replaced with a
    /// marker so the byte comparison below is about the markup the component authors. What the ids
    /// MEAN is pinned elsewhere and stays pinned: that the heading id and the list's
    /// <c>aria-labelledby</c> are the same string is
    /// <see cref="FormidableSummaryTests.A_heading_is_associated_with_its_list"/>'s assertion.
    /// </summary>
    private static string Stabilize(string markup) =>
        Regex.Replace(
            Regex.Replace(markup, "formidable-[0-9a-f]{8}-", "formidable-#-"),
            "blazor:onclick=\"[0-9]+\"",
            "blazor:onclick=\"#\"");

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderSummary(
        Action<RenderTreeBuilder, int> configure,
        EngineOrder? model = null)
    {
        var order = model ?? new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            // Nothing in these tests renders a field, so without the override every client submit
            // error would be filtered out as belonging to something unrendered and the summary
            // would have nothing to list.
            builder.AddComponentParameter(2, "Options", new FormidableOptions { DisclosureOverride = _ => true });
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.FocusFirstErrorOnInvalidSubmit), false);
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                configure(inner, 1);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<EngineOrder>>();
    }

    private static void Submit(IRenderedComponent<FormidableForm<EngineOrder>> form)
    {
        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.NotEmpty(form.FindAll(".formidable-summary__band")));
    }

    private static IReadOnlyList<string> EntryTexts(
        IRenderedComponent<FormidableForm<EngineOrder>> form, string bandSuffix) =>
        form.Find($"ul.formidable-summary__group--{bandSuffix}")
            .QuerySelectorAll("li.formidable-summary__item")
            .Select(item => item.TextContent)
            .ToList();

    /// <summary>
    /// The whole default rendering, byte for byte. Every parameter this file adds is additive, and
    /// this is what "additive" has to mean at the markup level: a form that sets none of them gets
    /// the summary it always got, down to the attribute order.
    /// </summary>
    [Fact]
    public async Task Defaults_render_the_markup_they_always_have()
    {
        var form = RenderSummary((inner, sequence) =>
            inner.AddComponentParameter(sequence, nameof(FormidableSummary.ErrorsHeading), "Fix these first"));

        Submit(form);

        const string expected =
            "<div class=\"formidable-summary\">" +
                "<div class=\"formidable-summary__region formidable-summary__region--errors\" role=\"alert\">" +
                    "<div class=\"formidable-summary__band formidable-summary__band--error\">" +
                        "<h2 id=\"formidable-#-errors-heading\" class=\"formidable-summary__heading\">Fix these first</h2>" +
                        "<ul class=\"formidable-summary__group formidable-summary__group--error\" aria-labelledby=\"formidable-#-errors-heading\">" +
                            "<li class=\"formidable-summary__item\">" +
                                "<button type=\"button\" class=\"formidable-summary__link\" blazor:onclick=\"#\">Description is required</button>" +
                            "</li>" +
                            "<li class=\"formidable-summary__item\">" +
                                "<button type=\"button\" class=\"formidable-summary__link\" blazor:onclick=\"#\">Description is too short</button>" +
                            "</li>" +
                            "<li class=\"formidable-summary__item\">" +
                                "<button type=\"button\" class=\"formidable-summary__link\" blazor:onclick=\"#\">A customer is required</button>" +
                            "</li>" +
                        "</ul>" +
                    "</div>" +
                "</div>" +
                "<div class=\"formidable-summary__region formidable-summary__region--advisories\" role=\"status\">" +
                    "<div class=\"formidable-summary__band formidable-summary__band--warning\">" +
                        "<ul class=\"formidable-summary__group formidable-summary__group--warning\">" +
                            "<li class=\"formidable-summary__item\">" +
                                "<button type=\"button\" class=\"formidable-summary__link\" blazor:onclick=\"#\">Description could be clearer</button>" +
                            "</li>" +
                        "</ul>" +
                    "</div>" +
                    "<div class=\"formidable-summary__band formidable-summary__band--info\">" +
                        "<ul class=\"formidable-summary__group formidable-summary__group--info\">" +
                            "<li class=\"formidable-summary__item\">" +
                                "<button type=\"button\" class=\"formidable-summary__link\" blazor:onclick=\"#\">Descriptions help reviewers</button>" +
                            "</li>" +
                        "</ul>" +
                    "</div>" +
                "</div>" +
            "</div>";

        Assert.Equal(expected, Stabilize(form.Find("div.formidable-summary").OuterHtml));

        await Services.DisposeAsync();
    }

    /// <summary>
    /// One field failing two rules is one entry under grouping, and it is the FIRST of its issues
    /// that survives — the message and the display name both come from that one, so a template
    /// showing either gets the same answer the click resolves to.
    /// </summary>
    [Fact]
    public async Task Grouping_collapses_a_field_that_failed_twice_into_one_entry()
    {
        var form = RenderSummary((inner, sequence) =>
            inner.AddComponentParameter(sequence, nameof(FormidableSummary.GroupByField), true));

        Submit(form);

        Assert.Equal(["Description is required", "A customer is required"], EntryTexts(form, "error"));

        await Services.DisposeAsync();
    }

    /// <summary>
    /// The counting question, and the one this file exists to settle: a cap of two over a band
    /// holding three issues on two fields lets BOTH fields through, because grouping happens first
    /// and the cap counts what grouping left. A cap applied to the issues instead would spend both
    /// of its slots on Description and never mention the customer.
    /// </summary>
    [Fact]
    public async Task A_cap_under_grouping_counts_fields_rather_than_issues()
    {
        var form = RenderSummary((inner, sequence) =>
        {
            inner.AddComponentParameter(sequence, nameof(FormidableSummary.GroupByField), true);
            inner.AddComponentParameter(sequence + 1, nameof(FormidableSummary.MaxItems), (int?)2);
        });

        Submit(form);

        Assert.Equal(["Description is required", "A customer is required"], EntryTexts(form, "error"));

        await Services.DisposeAsync();
    }

    /// <summary>
    /// The same cap without grouping, which is the control the test above needs: the two slots go
    /// to the two Description issues and the customer's error is what falls off the end. Both
    /// tests read the same band of the same form, so the only thing that can explain the
    /// difference between them is the parameter.
    /// </summary>
    [Fact]
    public async Task A_cap_without_grouping_counts_issues()
    {
        var form = RenderSummary((inner, sequence) =>
            inner.AddComponentParameter(sequence, nameof(FormidableSummary.MaxItems), (int?)2));

        Submit(form);

        Assert.Equal(["Description is required", "Description is too short"], EntryTexts(form, "error"));

        await Services.DisposeAsync();
    }

    /// <summary>
    /// A capped band with no overflow template renders nothing whatever in place of what it
    /// dropped: no element, no class, no sentence. What stands in for dropped entries has a
    /// language and a plural rule behind it, and the component picks neither.
    /// </summary>
    [Fact]
    public async Task A_cap_with_no_overflow_template_renders_nothing_in_place_of_what_it_dropped()
    {
        var form = RenderSummary((inner, sequence) =>
            inner.AddComponentParameter(sequence, nameof(FormidableSummary.MaxItems), (int?)1));

        Submit(form);

        var list = form.Find("ul.formidable-summary__group--error");
        Assert.Single(list.Children);
        Assert.Empty(form.FindAll(".formidable-summary__overflow"));
        // Text, not just elements: a bare text node in the list would pass an element count and is
        // exactly the shape a shipped "and 2 more" would take if somebody wrote one.
        Assert.Equal("Description is required", list.TextContent);

        await Services.DisposeAsync();
    }

    /// <summary>
    /// Set, the template gets the number the band held back — two of the error band's three, and
    /// nothing at all from the advisory bands, which suppressed nothing and so never reach it.
    /// </summary>
    [Fact]
    public async Task An_overflow_template_receives_the_number_the_band_held_back()
    {
        var counts = new List<int>();
        RenderFragment<int> overflow = suppressed => builder =>
        {
            counts.Add(suppressed);
            builder.AddContent(0, $"and {suppressed} more");
        };

        var form = RenderSummary((inner, sequence) =>
        {
            inner.AddComponentParameter(sequence, nameof(FormidableSummary.MaxItems), (int?)1);
            inner.AddComponentParameter(sequence + 1, nameof(FormidableSummary.OverflowTemplate), overflow);
        });

        Submit(form);

        var overflowItem = form.Find("li.formidable-summary__overflow");
        Assert.Equal("and 2 more", overflowItem.TextContent);
        Assert.Equal("ul", overflowItem.ParentElement!.TagName.ToLowerInvariant());
        // The summary re-renders on every state change, so the template is invoked once per band
        // per render rather than once per band: what the list pins is that no invocation ever
        // carried a number other than the two the error band held back, and the DOM pins that the
        // advisory bands, which suppressed nothing, produced no overflow item of their own.
        Assert.NotEmpty(counts);
        Assert.All(counts, count => Assert.Equal(2, count));
        Assert.Single(form.FindAll(".formidable-summary__overflow"));

        await Services.DisposeAsync();
    }

    /// <summary>
    /// Zero is a cap, not a mistake: the band's list renders with no entries in it and the whole
    /// band is offered to the overflow template. That is how a summary asks for the count alone.
    /// </summary>
    [Fact]
    public async Task A_cap_of_zero_renders_an_empty_list_and_offers_the_whole_count()
    {
        RenderFragment<int> overflow = suppressed => builder => builder.AddContent(0, $"{suppressed} to fix");

        var form = RenderSummary((inner, sequence) =>
        {
            inner.AddComponentParameter(sequence, nameof(FormidableSummary.MaxItems), (int?)0);
            inner.AddComponentParameter(sequence + 1, nameof(FormidableSummary.OverflowTemplate), overflow);
        });

        Submit(form);

        Assert.Empty(form.FindAll("li.formidable-summary__item"));
        Assert.Equal("3 to fix", form.Find("ul.formidable-summary__group--error li.formidable-summary__overflow").TextContent);

        await Services.DisposeAsync();
    }

    /// <summary>A cap no band reaches leaves every band exactly as an uncapped summary renders it.</summary>
    [Fact]
    public async Task A_cap_above_the_entry_count_changes_nothing()
    {
        RenderFragment<int> overflow = suppressed => builder => builder.AddContent(0, $"and {suppressed} more");

        var form = RenderSummary((inner, sequence) =>
        {
            inner.AddComponentParameter(sequence, nameof(FormidableSummary.MaxItems), (int?)99);
            inner.AddComponentParameter(sequence + 1, nameof(FormidableSummary.OverflowTemplate), overflow);
        });

        Submit(form);

        Assert.Equal(
            ["Description is required", "Description is too short", "A customer is required"],
            EntryTexts(form, "error"));
        Assert.Empty(form.FindAll(".formidable-summary__overflow"));

        await Services.DisposeAsync();
    }

    /// <summary>
    /// A negative cap throws rather than emptying the list, for the reason HeadingLevel throws: it
    /// is a number a page computes, and the arithmetic having gone below zero is the finding.
    /// </summary>
    [Fact]
    public async Task A_negative_cap_throws()
    {
        var exception = Assert.ThrowsAny<Exception>(() => RenderSummary((inner, sequence) =>
            inner.AddComponentParameter(sequence, nameof(FormidableSummary.MaxItems), (int?)(-1))));

        Assert.Contains(nameof(FormidableSummary.MaxItems), exception.Message);

        await Services.DisposeAsync();
    }

    /// <summary>
    /// Grouping runs inside a band and never across the summary, so a field that failed an error
    /// rule and an advisory rule is listed once under each. One field described two ways is not
    /// one description repeated.
    /// </summary>
    [Fact]
    public async Task Grouping_lists_a_field_once_in_each_band_it_appears_in()
    {
        var form = RenderSummary((inner, sequence) =>
            inner.AddComponentParameter(sequence, nameof(FormidableSummary.GroupByField), true));

        Submit(form);

        Assert.Equal(["Description is required", "A customer is required"], EntryTexts(form, "error"));
        Assert.Equal(["Description could be clearer"], EntryTexts(form, "warning"));
        Assert.Equal(["Descriptions help reviewers"], EntryTexts(form, "info"));

        await Services.DisposeAsync();
    }

    /// <summary>
    /// The cap is per band, so an error band trimmed to one entry does not spend the advisory
    /// bands' allowance: each band counts its own.
    /// </summary>
    [Fact]
    public async Task The_cap_applies_to_each_band_separately()
    {
        var form = RenderSummary((inner, sequence) =>
            inner.AddComponentParameter(sequence, nameof(FormidableSummary.MaxItems), (int?)1));

        Submit(form);

        Assert.Single(EntryTexts(form, "error"));
        Assert.Equal(["Description could be clearer"], EntryTexts(form, "warning"));
        Assert.Equal(["Descriptions help reviewers"], EntryTexts(form, "info"));

        await Services.DisposeAsync();
    }

    /// <summary>
    /// A template decides what an entry READS as and nothing else: the button, its type, its
    /// class, its place inside the list item and what a click on it does all survive it. The
    /// template here renders each issue's display name, which is the dogfood's whole ask — and
    /// note that the two Description entries carry different names, so a summary grouping by name
    /// would still show two.
    /// </summary>
    [Fact]
    public async Task An_item_template_replaces_the_button_content_and_nothing_else()
    {
        RenderFragment<VisibleIssue> template = entry => builder => builder.AddContent(0, entry.Issue.DisplayName);

        var form = RenderSummary((inner, sequence) =>
            inner.AddComponentParameter(sequence, nameof(FormidableSummary.ItemTemplate), template));

        Submit(form);

        var buttons = form.Find("ul.formidable-summary__group--error").QuerySelectorAll("li.formidable-summary__item > button");
        Assert.Equal(
            ["Order description", "Description", "Customer"],
            buttons.Select(button => button.TextContent).ToList());
        Assert.All(buttons, button =>
        {
            Assert.Equal("button", button.GetAttribute("type"));
            Assert.Equal("formidable-summary__link", button.GetAttribute("class"));
        });

        buttons[0].Click();

        var field = Assert.Single(_focus.Requests);
        Assert.Equal(nameof(EngineOrder.Description), field.FieldName);

        await Services.DisposeAsync();
    }

    /// <summary>
    /// The control for the test below, stated here rather than assumed: with no activation
    /// callback, a click asks the focus service for the clicked entry's field.
    /// </summary>
    [Fact]
    public async Task Without_an_activation_callback_a_click_still_moves_focus()
    {
        var form = RenderSummary((_, _) => { });

        Submit(form);
        form.FindAll("button.formidable-summary__link")[2].Click();

        var field = Assert.Single(_focus.Requests);
        Assert.Equal(nameof(EngineOrder.Customer), field.FieldName);

        await Services.DisposeAsync();
    }

    /// <summary>
    /// Set, the callback replaces the focus move outright — it receives the clicked entry whole,
    /// field and issue together, and the focus service is not asked at all.
    /// </summary>
    [Fact]
    public async Task An_activation_callback_replaces_the_focus_move()
    {
        var activated = new List<VisibleIssue>();
        var form = RenderSummary((inner, sequence) =>
            inner.AddComponentParameter(
                sequence,
                nameof(FormidableSummary.OnItemActivated),
                EventCallback.Factory.Create<VisibleIssue>(this, activated.Add)));

        Submit(form);
        form.FindAll("button.formidable-summary__link")[2].Click();

        var entry = Assert.Single(activated);
        Assert.Equal(nameof(EngineOrder.Customer), entry.Field.FieldName);
        Assert.Equal("A customer is required", entry.Issue.Message);
        Assert.Empty(_focus.Requests);

        await Services.DisposeAsync();
    }

    /// <summary>
    /// And it takes the two focus-move parameters with it. Both exist to serve an attempt this
    /// callback prevents, so a page wiring all three gets its callback and nothing else — which is
    /// the reason the docs point a dialog at PrepareFocus instead of here.
    /// </summary>
    [Fact]
    public async Task An_activation_callback_takes_the_prepare_hook_and_the_fallback_with_it()
    {
        _focus.Lands = false; // A miss is what would reach the fallback, if anything did.
        var prepared = 0;
        var recovered = 0;
        var activated = 0;

        var form = RenderSummary((inner, sequence) =>
        {
            inner.AddComponentParameter(
                sequence,
                nameof(FormidableSummary.OnItemActivated),
                EventCallback.Factory.Create<VisibleIssue>(this, _ => activated++));
            inner.AddComponentParameter(
                sequence + 1,
                nameof(FormidableSummary.PrepareFocus),
                (Func<FieldIdentifier, ValueTask>)(_ =>
                {
                    prepared++;
                    return ValueTask.CompletedTask;
                }));
            inner.AddComponentParameter(
                sequence + 2,
                nameof(FormidableSummary.FocusFallback),
                (Func<FieldIdentifier, ValueTask<bool>>)(_ =>
                {
                    recovered++;
                    return ValueTask.FromResult(true);
                }));
        });

        Submit(form);
        form.FindAll("button.formidable-summary__link")[0].Click();

        Assert.Equal(1, activated);
        Assert.Equal(0, prepared);
        Assert.Equal(0, recovered);
        Assert.Empty(_focus.Requests);

        await Services.DisposeAsync();
    }
}
