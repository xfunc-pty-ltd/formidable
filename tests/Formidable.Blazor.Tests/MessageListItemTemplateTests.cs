using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the item template on the three inline message lists. Unset, each item holds the message as
/// plain text, with no element around it. Set, the template renders inside each item the
/// component still owns (its severity classes, and the list's id and class), and it is handed the
/// component's own field with the name the summary gives the issue.
/// </summary>
public class MessageListItemTemplateTests : BunitContext
{
    private const string ReVoicedModelLevelName = "Ce formulaire";

    private static readonly ValidationIssue ReferenceError =
        new(nameof(ItemTemplateTicket.Reference), "A reference is required", DisplayName: "Ticket reference");

    private static readonly ValidationIssue ReferenceWarning =
        new(nameof(ItemTemplateTicket.Reference), "References usually start TKT-", ValidationSeverity.Warning, DisplayName: "Ticket reference");

    private static readonly ValidationIssue ReferenceInfo =
        new(nameof(ItemTemplateTicket.Reference), "Old references still resolve", ValidationSeverity.Info, DisplayName: "Ticket reference");

    public MessageListItemTemplateTests() => Services.AddFormidable();

    private IRenderedComponent<FormidableForm<ItemTemplateTicket>> RenderForm(
        ItemTemplateTicket ticket,
        IModelValidator<ItemTemplateTicket> validator,
        Action<RenderTreeBuilder> inner,
        FormidableOptions? options = null)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<ItemTemplateTicket>>(0);
            builder.AddComponentParameter(1, "Model", ticket);
            builder.AddComponentParameter(2, "Validator", validator);
            builder.AddComponentParameter(3, "Options", options ?? new FormidableOptions { DisclosureOverride = _ => true });
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => b => inner(b)));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<ItemTemplateTicket>>();
    }

    private static void FieldMessage(
        RenderTreeBuilder builder,
        ItemTemplateTicket ticket,
        RenderFragment<VisibleIssue>? template,
        IReadOnlyDictionary<string, object>? attributes = null)
    {
        builder.OpenComponent<FormidableFieldMessage<string>>(10);
        builder.AddComponentParameter(11, "For", (Expression<Func<string>>)(() => ticket.Reference));
        if (template is not null)
        {
            builder.AddComponentParameter(12, "ItemTemplate", template);
        }

        if (attributes is not null)
        {
            builder.AddMultipleAttributes(13, attributes);
        }

        builder.CloseComponent();
    }

    private static void ModelMessage(
        RenderTreeBuilder builder,
        RenderFragment<VisibleIssue>? template,
        IReadOnlyDictionary<string, object>? attributes = null)
    {
        builder.OpenComponent<FormidableModelMessage>(20);
        if (template is not null)
        {
            builder.AddComponentParameter(21, "ItemTemplate", template);
        }

        if (attributes is not null)
        {
            builder.AddMultipleAttributes(22, attributes);
        }

        builder.CloseComponent();
    }

    private static string FieldListId(ItemTemplateTicket ticket, string fieldName) =>
        FormidableFieldId.MessagesFor(new FieldIdentifier(ticket, fieldName));

    // Records what the template was handed, and renders the name ahead of the message.
    private static RenderFragment<VisibleIssue> Recording(List<VisibleIssue> seen) => item =>
    {
        seen.Add(item);
        return builder => builder.AddContent(0, item.DisplayName + ": " + item.Issue.Message);
    };

    [Fact]
    public void No_template_renders_each_message_as_plain_text_in_its_item()
    {
        // Mutation: the no-template branch of FormidableMessageList.Render wraps the message in a
        // span, and both lists' markup moves off the plain-text items pinned below.
        var ticket = new ItemTemplateTicket();
        var form = RenderForm(
            ticket,
            new FixedReportValidator<ItemTemplateTicket>(ReferenceError, ReferenceWarning, ReferenceInfo, new ValidationIssue(string.Empty, "The ticket is incomplete")),
            b =>
            {
                FieldMessage(b, ticket, template: null);
                ModelMessage(b, template: null);
            });
        var fieldListId = FieldListId(ticket, nameof(ItemTemplateTicket.Reference));

        form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.Equal(3, form.FindAll($"#{fieldListId} li").Count));

        Assert.Equal(
            $"<ul id=\"{fieldListId}\" class=\"formidable-message-list\">"
            + "<li class=\"formidable-message formidable-message--error\">A reference is required</li>"
            + "<li class=\"formidable-message formidable-message--warning\">References usually start TKT-</li>"
            + "<li class=\"formidable-message formidable-message--info\">Old references still resolve</li>"
            + "</ul>",
            form.Find($"#{fieldListId}").OuterHtml);

        var modelListId = FieldListId(ticket, string.Empty);
        Assert.Equal(
            $"<ul id=\"{modelListId}\" class=\"formidable-message-list\">"
            + "<li class=\"formidable-message formidable-message--error\">The ticket is incomplete</li>"
            + "</ul>",
            form.Find($"#{modelListId}").OuterHtml);
    }

    [Fact]
    public void The_template_renders_inside_each_severity_classed_item()
    {
        // Mutation: FormidableMessageList.Render adds issue.Message where it should add the
        // template, and each item loses its severity word.
        var ticket = new ItemTemplateTicket();
        var cut = Render<SeverityWordMessageHost>(parameters => parameters
            .Add(p => p.Model, ticket)
            .Add(p => p.Validator, new FixedReportValidator<ItemTemplateTicket>(ReferenceError, ReferenceWarning, ReferenceInfo))
            .Add(p => p.Options, new FormidableOptions { DisclosureOverride = _ => true }));
        var form = cut.FindComponent<FormidableForm<ItemTemplateTicket>>();
        var fieldListId = FieldListId(ticket, nameof(ItemTemplateTicket.Reference));

        form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.Equal(3, form.FindAll($"#{fieldListId} li").Count));

        var items = form.FindAll($"#{fieldListId} li");
        Assert.Collection(
            items,
            error =>
            {
                Assert.Equal("formidable-message formidable-message--error", error.GetAttribute("class"));
                Assert.Equal("<strong>Error:</strong> A reference is required", error.InnerHtml);
            },
            warning =>
            {
                Assert.Equal("formidable-message formidable-message--warning", warning.GetAttribute("class"));
                Assert.Equal("<strong>Warning:</strong> References usually start TKT-", warning.InnerHtml);
            },
            info =>
            {
                Assert.Equal("formidable-message formidable-message--info", info.GetAttribute("class"));
                Assert.Equal("<strong>Note:</strong> Old references still resolve", info.InnerHtml);
            });
    }

    [Fact]
    public void The_field_list_hands_the_template_its_own_field_and_name()
    {
        // Mutations: Render hands the template default in place of the component's field, and
        // Field no longer equals the Reference identifier; or it builds the VisibleIssue without
        // DisplayName, and the unnamed error reads null where the summary would say "Reference".
        var ticket = new ItemTemplateTicket();
        var seen = new List<VisibleIssue>();
        var form = RenderForm(
            ticket,
            new FixedReportValidator<ItemTemplateTicket>(ReferenceError, new ValidationIssue(nameof(ItemTemplateTicket.Reference), "That reference is already open")),
            b => FieldMessage(b, ticket, Recording(seen)));
        var fieldListId = FieldListId(ticket, nameof(ItemTemplateTicket.Reference));

        form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.Equal(2, form.FindAll($"#{fieldListId} li").Count));

        var reference = new FieldIdentifier(ticket, nameof(ItemTemplateTicket.Reference));
        var handed = seen.TakeLast(2).ToList();
        Assert.All(handed, item => Assert.Equal(reference, item.Field));
        Assert.Equal(["Ticket reference", "Reference"], handed.Select(item => item.DisplayName));
        Assert.Equal(
            ["Ticket reference: A reference is required", "Reference: That reference is already open"],
            form.FindAll($"#{fieldListId} li").Select(item => item.TextContent));
    }

    [Fact]
    public void The_collection_list_hands_the_template_the_collection_field()
    {
        // Mutation: Render hands the template default in place of the component's field, and
        // Field no longer equals the Tags identifier.
        var ticket = new ItemTemplateTicket();
        var seen = new List<VisibleIssue>();
        RenderFragment<VisibleIssue> template = Recording(seen);

        // No disclosure override: the collection message registers its own path, which is what
        // lets the submit show the rule's message.
        var form = RenderForm(
            ticket,
            new FixedReportValidator<ItemTemplateTicket>(new ValidationIssue(nameof(ItemTemplateTicket.Tags), "Add at least one tag")),
            b =>
            {
                b.OpenComponent<FormidableCollectionMessage<List<string>>>(30);
                b.AddComponentParameter(31, "For", (Expression<Func<List<string>>>)(() => ticket.Tags));
                b.AddComponentParameter(32, "ItemTemplate", template);
                b.CloseComponent();
            },
            new FormidableOptions());
        var tagsListId = FieldListId(ticket, nameof(ItemTemplateTicket.Tags));

        form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.Single(form.FindAll($"#{tagsListId} li")));

        var item = seen.Last();
        Assert.Equal(new FieldIdentifier(ticket, nameof(ItemTemplateTicket.Tags)), item.Field);
        Assert.Equal(nameof(ItemTemplateTicket.Tags), item.DisplayName);
        Assert.Equal("Tags: Add at least one tag", form.Find($"#{tagsListId} li").TextContent);
    }

    [Fact]
    public void The_model_list_hands_the_template_the_model_level_field_and_name()
    {
        // Mutations: Render builds the VisibleIssue without DisplayName, and the gate's entry
        // reads null; or the model list hands the shipped name rather than the option's, and it
        // reads "This form".
        var ticket = new ItemTemplateTicket();
        var seen = new List<VisibleIssue>();
        var options = new FormidableOptions { ModelLevelDisplayName = ReVoicedModelLevelName };

        // Requester fails and nothing renders it, so the blocked submit can show only the gate's
        // explanation, on the model-level field.
        var form = RenderForm(
            ticket,
            new FixedReportValidator<ItemTemplateTicket>(new ValidationIssue(nameof(ItemTemplateTicket.Requester), "A requester is required")),
            b => ModelMessage(b, Recording(seen)),
            options);
        var modelListId = FieldListId(ticket, string.Empty);

        form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.Single(form.FindAll($"#{modelListId} li")));

        var item = seen.Last();
        Assert.Equal(new FieldIdentifier(ticket, string.Empty), item.Field);
        Assert.Equal(options.DefensiveGateMessage, item.Issue.Message);
        Assert.Null(item.Issue.DisplayName);
        Assert.Equal(ReVoicedModelLevelName, item.DisplayName);
        Assert.Equal(
            ReVoicedModelLevelName + ": " + options.DefensiveGateMessage,
            form.Find($"#{modelListId} li").TextContent);
    }

    [Fact]
    public void The_list_keeps_its_id_and_class_under_a_template()
    {
        // Mutation: Render adds the list's id only when no template is set, and both lists lose
        // the id every input's aria-describedby points at.
        var ticket = new ItemTemplateTicket();
        var seen = new List<VisibleIssue>();
        var splat = new Dictionary<string, object> { ["class"] = "page-list", ["id"] = "chosen-by-the-page" };
        var form = RenderForm(
            ticket,
            new FixedReportValidator<ItemTemplateTicket>(ReferenceError, new ValidationIssue(string.Empty, "The ticket is incomplete")),
            b =>
            {
                FieldMessage(b, ticket, Recording(seen), splat);
                ModelMessage(b, Recording(seen), splat);
            });
        var fieldListId = FieldListId(ticket, nameof(ItemTemplateTicket.Reference));
        var modelListId = FieldListId(ticket, string.Empty);

        form.InvokeAsync(() => form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.Single(form.FindAll($"#{fieldListId} li")));

        var lists = form.FindAll("ul.formidable-message-list");
        Assert.Equal([fieldListId, modelListId], lists.Select(list => list.GetAttribute("id")));
        Assert.All(lists, list => Assert.Equal("page-list formidable-message-list", list.GetAttribute("class")));
        Assert.Equal("Ticket reference: A reference is required", form.Find($"#{fieldListId} li").TextContent);
        Assert.Equal("This form: The ticket is incomplete", form.Find($"#{modelListId} li").TextContent);
    }
}
