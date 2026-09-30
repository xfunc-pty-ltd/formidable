using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// An inline message item that outlives a pass has to be the SAME item afterwards, template
/// content included. Blazor matches unkeyed siblings by position, so clearing a field's first
/// message would hand the first item's template instance to the message that moved up into its
/// place. The items carry a key for that reason, and these pin what the key owes: it tracks an
/// item across renders, and it stays unique among siblings when two issues key alike.
/// </summary>
/// <remarks>
/// A rendered element's identity is not observable from bUnit, but a child component's is, and a
/// keyed item carries its components with it: <see cref="ItemWitness"/> renders inside
/// <c>ItemTemplate</c>, and its instance is the item's identity made visible.
/// </remarks>
public class MessageListItemKeyTests : BunitContext
{
    private const string FirstProblem = "A reference is required";

    private const string SecondProblem = "References start TKT-";

    /// <summary>A template component that records each instance of itself as it initialises.</summary>
    private sealed class ItemWitness : ComponentBase
    {
        [Parameter] public string Label { get; set; } = string.Empty;

        [Parameter] public List<ItemWitness> Created { get; set; } = [];

        protected override void OnInitialized() => Created.Add(this);

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddContent(0, Label);
    }

    /// <summary>A plain class for a <c>WithState</c>-style payload, which compares by reference.</summary>
    private sealed class StatePayload
    {
    }

    /// <summary>Answers every profile with <see cref="Issues"/>, which a test replaces between submits.</summary>
    private sealed class SwitchableTicketReport(params ValidationIssue[] issues) : IModelValidator<ItemTemplateTicket>
    {
        public ValidationIssue[] Issues { get; set; } = issues;

        public Task<ValidationReport> ValidateAsync(
            ItemTemplateTicket model,
            ValidationProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Validate(model, profile));

        public ValidationReport Validate(ItemTemplateTicket model, ValidationProfile profile) => new(Issues);
    }

    public MessageListItemKeyTests() => Services.AddFormidable();

    private IRenderedComponent<FormidableForm<ItemTemplateTicket>> RenderReferenceList(
        ItemTemplateTicket ticket,
        IModelValidator<ItemTemplateTicket> validator,
        List<ItemWitness>? created)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<ItemTemplateTicket>>(0);
            builder.AddComponentParameter(1, "Model", ticket);
            builder.AddComponentParameter(2, "Validator", validator);
            builder.AddComponentParameter(3, "Options", new FormidableOptions { DisclosureOverride = _ => true });
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableFieldMessage<string>>(0);
                inner.AddComponentParameter(1, "For", (Expression<Func<string>>)(() => ticket.Reference));
                if (created is not null)
                {
                    inner.AddComponentParameter(2, "ItemTemplate", (RenderFragment<VisibleIssue>)(item => content =>
                    {
                        content.OpenComponent<ItemWitness>(0);
                        content.AddComponentParameter(1, nameof(ItemWitness.Label), item.Issue.Message);
                        content.AddComponentParameter(2, nameof(ItemWitness.Created), created);
                        content.CloseComponent();
                    }));
                }

                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<ItemTemplateTicket>>();
    }

    private static string ReferenceListId(ItemTemplateTicket ticket) =>
        FormidableFieldId.MessagesFor(new FieldIdentifier(ticket, nameof(ItemTemplateTicket.Reference)));

    private static void Submit(IRenderedComponent<FormidableForm<ItemTemplateTicket>> form, ItemTemplateTicket ticket, int expectedItems)
    {
        _ = form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() =>
            Assert.Equal(expectedItems, form.FindAll($"#{ReferenceListId(ticket)} li").Count));
    }

    private static ValidationIssue Reference(string message, object? state = null) =>
        new(nameof(ItemTemplateTicket.Reference), message, State: state);

    /// <summary>
    /// The message that stays keeps the template instance it had when the one above it clears.
    /// Mutations that must break it, both run: drop the <c>SetKey</c> call from the item loop, and
    /// the surviving message is handed the cleared one's instance; or number the item frames with
    /// a running counter across the list, and the surviving item's template content is rebuilt
    /// under the item that kept its key.
    /// </summary>
    [Fact]
    public void A_template_instance_follows_its_own_message_when_an_earlier_one_clears()
    {
        var ticket = new ItemTemplateTicket();
        var created = new List<ItemWitness>();
        var report = new SwitchableTicketReport(Reference(FirstProblem), Reference(SecondProblem));
        var form = RenderReferenceList(ticket, report, created);

        Submit(form, ticket, expectedItems: 2);
        var before = form.FindComponents<ItemWitness>().Select(c => c.Instance).ToList();
        Assert.Equal([FirstProblem, SecondProblem], before.Select(w => w.Label));

        report.Issues = [Reference(SecondProblem)];
        Submit(form, ticket, expectedItems: 1);

        var after = Assert.Single(form.FindComponents<ItemWitness>()).Instance;
        Assert.Equal(SecondProblem, after.Label);

        // Reference identity: the very instance that rendered the second message before, and no
        // instance made since.
        Assert.Same(before[1], after);
        Assert.Equal(2, created.Count);
    }

    /// <summary>
    /// A pin of the occurrence in the key. Two equal issues key alike, so the occurrence is what
    /// keeps their items' keys apart. The second submit renders the list again over the same two
    /// items, so the pin covers a re-render as well as the first paint. Mutation that must break
    /// it: key each item by its issue key without the occurrence, and the submit throws "More than
    /// one sibling of element 'li' has the same key value".
    /// </summary>
    [Fact]
    public void An_inline_list_holding_two_equal_issues_renders_both_and_survives_the_next_render()
    {
        var ticket = new ItemTemplateTicket();
        var form = RenderReferenceList(ticket, new FixedReportValidator<ItemTemplateTicket>(Reference(FirstProblem), Reference(FirstProblem)), created: null);

        Submit(form, ticket, expectedItems: 2);
        Submit(form, ticket, expectedItems: 2);

        Assert.Equal(
            [FirstProblem, FirstProblem],
            form.FindAll($"#{ReferenceListId(ticket)} li").Select(li => li.TextContent));
    }

    /// <summary>
    /// A pin of the agreement between the key and its count. Two issues alike in everything but a
    /// class-payload state are unequal records that key alike, so their occurrence alone tells
    /// their items apart, and it has to be counted on the key the item carries. Mutation that must
    /// break it: count occurrences by <c>(field, issue)</c> while keying by <c>IssueKey</c>, and
    /// both items take occurrence 0 under one key, so the submit throws "More than one sibling of
    /// element 'li' has the same key value".
    /// </summary>
    [Fact]
    public void Two_issues_differing_only_in_State_render_as_two_items_and_survive_the_next_render()
    {
        var ticket = new ItemTemplateTicket();
        var first = Reference(FirstProblem, new StatePayload());
        var second = Reference(FirstProblem, new StatePayload());
        Assert.NotEqual(first, second);

        var form = RenderReferenceList(ticket, new FixedReportValidator<ItemTemplateTicket>(first, second), created: null);

        Submit(form, ticket, expectedItems: 2);
        Submit(form, ticket, expectedItems: 2);

        Assert.Equal(
            [FirstProblem, FirstProblem],
            form.FindAll($"#{ReferenceListId(ticket)} li").Select(li => li.TextContent));
    }
}
