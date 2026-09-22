using Bunit;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

// Reproduces the Phase-1 walkthrough report "info message not shown on submit": an
// Info-severity rule that fails at submit must render in both FormidableFieldMessage
// (formidable-message--info) and the FormidableSummary info group. Pins the disclosure
// lifecycle for advisory severities at the component level.
public class SubmitSeverityRenderingTests : BunitContext
{
    private sealed class TaggedListing
    {
        public string Title { get; set; } = "ok";
        public string Tags { get; set; } = "a,b,c,d,e,f";
    }

    private sealed class TaggedListingValidator : DraftSubmitValidator<TaggedListing>
    {
        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(l => l.Tags)
                .Must(t => t.Length == 0 || t.Split(',').Length <= 5)
                .WithSeverity(Severity.Info)
                .WithMessage("More than five tags rarely helps discovery");
        }
    }

    private IRenderedComponent<FormidableForm<TaggedListing>> RenderWithMessageAndSummary(TaggedListing listing)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<TaggedListing>>(0);
            builder.AddComponentParameter(1, "Model", listing);
            builder.AddComponentParameter(2, "Options", new FormidableOptions { DisclosureOverride = _ => true });
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableInputText>(0);
                inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string?>>)(() => listing.Tags));
                inner.AddComponentParameter(2, "Value", listing.Tags);
                inner.AddComponentParameter(3, "ValueChanged",
                    EventCallback.Factory.Create<string?>(this, v => listing.Tags = v ?? string.Empty));
                inner.CloseComponent();

                inner.OpenComponent<FormidableFieldMessage<string>>(4);
                inner.AddComponentParameter(5, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => listing.Tags));
                inner.CloseComponent();

                inner.OpenComponent<FormidableSummary>(6);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<TaggedListing>>();
    }

    [Fact]
    public async Task An_info_issue_failing_at_submit_renders_in_message_list_and_summary()
    {
        Services.AddFormidableBlazor();
        Services.AddSingleton<IValidator<TaggedListing>, TaggedListingValidator>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        // The form resolves field order after every render that changed the registered set; answer
        // it explicitly rather than leaving it on the loose default, which is a null no answer.
        JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js")
            .Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);

        var model = new TaggedListing();
        var form = RenderWithMessageAndSummary(model);

        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            // 1. FormidableFieldMessage renders the info: a li.formidable-message--info with the message text.
            var messageItem = form.Find("li.formidable-message--info");
            Assert.Contains("More than five tags rarely helps discovery", messageItem.TextContent);

            // 2. FormidableSummary renders an info group: ul.formidable-summary__group--info with one item.
            var summaryGroup = form.Find("ul.formidable-summary__group--info");
            Assert.Single(summaryGroup.QuerySelectorAll("li"));
        });

        await Services.DisposeAsync();
    }
}
