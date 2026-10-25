using System.Collections;
using Bunit;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A row object of a collection with no indexer (a <see cref="HashSet{T}"/>, a
/// <see cref="LinkedList{T}"/>, a type known only by <see cref="IEnumerable{T}"/>), bound with
/// <c>() => row.Name</c>, is found by its position in the order the collection enumerates, which
/// is the order FluentValidation numbers it in. These pin that the row's message, its required
/// mark and its summary entry all reach the row at a blocked submit, and that a row an edit
/// empties is invalid and never valid.
/// </summary>
// The mutation the cases name: remove the walk from ReflectionModelIntrospector.GetIndexedValue,
// so Items[1].Name stops at the collection. The row then carries no required mark, shows no
// message, and wears the valid class once emptied, and the summary lists only the form's own line.
public class CollectionRowsByEnumerationTests : BunitContext
{
    public const string NameRequired = "Name required";

    private readonly FakeTimeProvider _clock = new();

    public CollectionRowsByEnumerationTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddFormidableBlazor();
    }

    public sealed class Row
    {
        public string? Name { get; set; }
    }

    /// <summary>A collection known only by <see cref="IEnumerable{T}"/>: no list interface and no indexer.</summary>
    public sealed class EnumerableOnlyRows(IEnumerable<Row> rows) : IEnumerable<Row>
    {
        private readonly List<Row> _rows = [.. rows];

        public IEnumerator<Row> GetEnumerator() => _rows.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class Roster<TRows>
        where TRows : IEnumerable<Row>
    {
        public required TRows Items { get; init; }
    }

    private sealed class RosterValidator<TRows> : AbstractValidator<Roster<TRows>>
        where TRows : IEnumerable<Row>
    {
        public RosterValidator() =>
            RuleForEach(m => (IEnumerable<Row>)m.Items)
                .ChildRules(row => row.RuleFor(r => r.Name).NotEmpty().WithMessage(NameRequired));
    }

    // The second row enumerated is empty, so its rule fails at Items[1].Name. Mutation that must
    // break it: the walk mutation, and the empty row has no aria-required, no message, and the
    // summary lists only the form's own line.
    [Theory]
    [InlineData("HashSet")]
    [InlineData("LinkedList")]
    [InlineData("EnumerableOnly")]
    public async Task A_blocked_submit_shows_a_failing_row_its_message_mark_and_summary_entry(string collection)
    {
        var rendered = RenderShape(collection, "ok", string.Empty);

        Assert.Equal("true", rendered.Required(0));
        Assert.Equal("true", rendered.Required(1));

        await rendered.Cut.InvokeAsync(() => rendered.Cut.Find("form").Submit());
        rendered.Cut.WaitForState(() => rendered.Engine.HasSubmitted && !rendered.Engine.IsValidating);
        await rendered.Cut.InvokeAsync(() => { });

        Assert.Equal([NameRequired], rendered.Messages(1));
        Assert.Empty(rendered.Messages(0));
        Assert.Equal([NameRequired], rendered.SummaryEntries());
        var failing = rendered.Items.ElementAt(1);
        Assert.Equal(string.Empty, failing.Name);
        var entry = Assert.Single(rendered.Engine.GetVisibleIssues());
        Assert.Equal(new FieldIdentifier(failing, nameof(Row.Name)), entry.Field);
    }

    // An edit that empties the second row enumerated turns that row invalid, and it never wears the
    // valid class. Mutation that must break it: the walk mutation, and the emptied row wears
    // formidable-valid with no message, because nothing names the row in the failure's place.
    [Theory]
    [InlineData("HashSet")]
    [InlineData("LinkedList")]
    [InlineData("EnumerableOnly")]
    public async Task An_edit_that_empties_a_row_turns_it_invalid_and_never_valid(string collection)
    {
        var rendered = RenderShape(collection, "ok", "ok");

        await rendered.Cut.InvokeAsync(() => rendered.Cut.Find("[data-entry=\"1\"] input").Change(string.Empty));
        rendered.Cut.WaitForState(() => !rendered.Engine.IsValidating);
        await rendered.Cut.InvokeAsync(() => _clock.Advance(new FormidableOptions().RefreshDebounce + TimeSpan.FromMilliseconds(1)));
        rendered.Cut.WaitForState(() => !rendered.Engine.IsValidating);

        var classes = rendered.Class(1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("formidable-valid", classes);
        Assert.Contains("formidable-invalid", classes);
    }

    /// <summary>Renders two rows named <paramref name="first"/> and <paramref name="second"/>, in that order, held in the collection shape <paramref name="collection"/> names.</summary>
    private Rendered RenderShape(string collection, string first, string second)
    {
        Row[] seed = [new() { Name = first }, new() { Name = second }];
        return collection switch
        {
            "HashSet" => RenderRows(new Roster<HashSet<Row>> { Items = [.. seed] }),
            "LinkedList" => RenderRows(new Roster<LinkedList<Row>> { Items = new(seed) }),
            _ => RenderRows(new Roster<EnumerableOnlyRows> { Items = new(seed) }),
        };
    }

    /// <summary>Renders a summary and, per row in the order the collection enumerates, a kit input and its message bound to <c>() => row.Name</c>, inside an element marked <c>data-entry</c>.</summary>
    private Rendered RenderRows<TRows>(Roster<TRows> model)
        where TRows : IEnumerable<Row>
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Roster<TRows>>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<Roster<TRows>>)new FluentValidationModelValidator<Roster<TRows>>(new RosterValidator<TRows>()));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                inner.CloseComponent();
                var index = 0;
                foreach (var row in model.Items)
                {
                    RenderRow(inner, index, row);
                    index++;
                }

                inner.AddMarkupContent(100, "<button type=\"submit\">Save</button>");
            }));
            builder.CloseComponent();
        });

        return new Rendered(cut, cut.FindComponent<FormidableForm<Roster<TRows>>>().Instance.Engine!, model.Items);
    }

    private void RenderRow(RenderTreeBuilder builder, int index, Row row)
    {
        builder.OpenRegion(1 + index);
        builder.OpenElement(0, "div");
        builder.SetKey(row);
        builder.AddAttribute(1, "data-entry", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.OpenComponent<FormidableInputText>(2);
        builder.AddComponentParameter(3, "Value", row.Name);
        builder.AddComponentParameter(4, "ValueChanged", EventCallback.Factory.Create<string?>(this, value => row.Name = value));
        builder.AddComponentParameter(5, "ValueExpression", (System.Linq.Expressions.Expression<Func<string?>>)(() => row.Name));
        builder.CloseComponent();
        builder.OpenComponent<FormidableFieldMessage<string?>>(6);
        builder.AddComponentParameter(7, "For", (System.Linq.Expressions.Expression<Func<string?>>)(() => row.Name));
        builder.CloseComponent();
        builder.CloseElement();
        builder.CloseRegion();
    }

    private sealed record Rendered(IRenderedComponent<IComponent> Cut, IFormidableEngine Engine, IEnumerable<Row> Items)
    {
        public string Required(int row) => Cut.Find($"[data-entry=\"{row}\"] input").GetAttribute("aria-required") ?? "-";

        public string Class(int row) => Cut.Find($"[data-entry=\"{row}\"] input").GetAttribute("class") ?? string.Empty;

        public IReadOnlyList<string> Messages(int row) =>
            Cut.FindAll($"[data-entry=\"{row}\"] li")
                .Select(element => element.TextContent.Trim())
                .ToList();

        public IReadOnlyList<string> SummaryEntries() =>
            Cut.FindAll(".formidable-summary .formidable-summary__item")
                .Select(element => element.TextContent.Trim())
                .ToList();
    }
}
