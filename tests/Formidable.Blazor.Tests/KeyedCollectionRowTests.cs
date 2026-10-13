using System.Collections.ObjectModel;
using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A <see cref="KeyedCollection{TKey, TItem}"/> is a list, so FluentValidation names a failing
/// row by its position, and an input bound by that position meets it. An input bound by a key that
/// is not an index, such as the string <c>"b"</c>, is named by the key's text, which no answer uses,
/// so it never wears the valid class. These pin both, beside a <c>List&lt;T&gt;</c> control.
/// </summary>
public class KeyedCollectionRowTests : BunitContext
{
    private const string CodeNeedsValue = "Code needs a value";
    private const string TitleNeedsValue = "Title needs a value";

    private readonly FakeTimeProvider _clock = new();

    public KeyedCollectionRowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddFormidableBlazor();
    }

    /// <summary>Codes keyed by the number before their colon.</summary>
    public sealed class IntKeyedCodes : KeyedCollection<int, string>
    {
        protected override int GetKeyForItem(string item) =>
            int.Parse(item.AsSpan(0, item.IndexOf(':', StringComparison.Ordinal)), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Codes keyed by the text before their colon.</summary>
    public sealed class StringKeyedCodes : KeyedCollection<string, string>
    {
        protected override string GetKeyForItem(string item) => item[..item.IndexOf(':', StringComparison.Ordinal)];
    }

    /// <summary>Codes keyed by the text before their colon, three classes down from <see cref="KeyedCollection{TKey, TItem}"/>.</summary>
    public sealed class LeafKeyedCodes : MidKeyedCodes
    {
    }

    public class MidKeyedCodes : BaseKeyedCodes
    {
    }

    public class BaseKeyedCodes : KeyedCollection<string, string>
    {
        protected override string GetKeyForItem(string item) => item[..item.IndexOf(':', StringComparison.Ordinal)];
    }

    /// <summary>Codes keyed by the text before their colon, with a title the collection's own type declares.</summary>
    public sealed class TitledCodes : KeyedCollection<string, string>
    {
        public string Title { get; set; } = "Roster";

        protected override string GetKeyForItem(string item) => item[..item.IndexOf(':', StringComparison.Ordinal)];
    }

    public sealed class CodesModel<TCodes>
        where TCodes : class, IList<string>
    {
        public required TCodes Codes { get; init; }
    }

    private sealed class CodesValidator<TCodes> : AbstractValidator<CodesModel<TCodes>>
        where TCodes : class, IList<string>
    {
        public CodesValidator() =>
            RuleForEach(m => (IEnumerable<string>)m.Codes).Must(code => !code.EndsWith(':')).WithMessage(CodeNeedsValue);
    }

    private sealed class TitledCodesValidator : AbstractValidator<CodesModel<TitledCodes>>
    {
        public TitledCodesValidator()
        {
            RuleForEach(m => (IEnumerable<string>)m.Codes).Must(code => !code.EndsWith(':')).WithMessage(CodeNeedsValue);
            RuleFor(m => m.Codes.Title).NotEmpty().WithMessage(TitleNeedsValue);
        }
    }

    // Keys "b" and "a" sit at positions 0 and 1. The visitor empties b's value, and FluentValidation
    // reports the failure at position 0, which no input bound by key names: the row shows no
    // message and, with the guard, no valid class either, before the submit and after it. A row
    // bound by a key that is not an index is never vouched for, the passing one included. The
    // collection is found however far down from KeyedCollection it derives. Mutations that must
    // break it: remove the KeyedCollection guard from SubmitCoverageTracker.WouldPassSubmit (both
    // cases: b wears formidable-valid with WouldPassSubmit true while its rule fails), or check
    // only the immediate base type in IsKeyedCollection (the three-deep case).
    [Theory]
    [InlineData("Direct")]
    [InlineData("ThreeDeep")]
    public Task A_KeyedCollection_row_bound_by_string_key_never_wears_the_valid_class(string derivation) =>
        derivation == "Direct"
            ? AssertKeyBoundRowsNeverGreenAsync(new CodesModel<StringKeyedCodes> { Codes = ["b:ok", "a:ok"] })
            : AssertKeyBoundRowsNeverGreenAsync(new CodesModel<LeafKeyedCodes> { Codes = ["b:ok", "a:ok"] });

    private async Task AssertKeyBoundRowsNeverGreenAsync<TCodes>(CodesModel<TCodes> model)
        where TCodes : KeyedCollection<string, string>
    {
        var invalid = 0;
        var rows = RenderCodes(
            model,
            [("b", () => model.Codes["b"], v => ReplaceByKey(model.Codes, "b", v)), ("a", () => model.Codes["a"], v => ReplaceByKey(model.Codes, "a", v))],
            () => invalid++);

        await rows.Cut.InvokeAsync(() => rows.Input("b").Change("b:"));
        Settle(rows);

        Assert.Empty(rows.Messages("b"));
        Assert.DoesNotContain("formidable-valid", rows.Class("b").Split(' '));
        Assert.False(rows.Engine.GetFieldState(FieldIdentifier.Create(() => model.Codes["b"])).WouldPassSubmit);

        await SubmitAsync(rows);

        Assert.Equal(1, invalid);
        Assert.Empty(rows.Messages("b"));
        Assert.DoesNotContain("formidable-valid", rows.Class("b").Split(' '));
        Assert.False(rows.Engine.GetFieldState(FieldIdentifier.Create(() => model.Codes["b"])).WouldPassSubmit);
        Assert.False(rows.Engine.GetFieldState(FieldIdentifier.Create(() => model.Codes["a"])).WouldPassSubmit);
        Assert.Contains(rows.Engine.Options.DefensiveGateMessage, rows.Cut.Markup, StringComparison.Ordinal);
    }

    // Bound by position, a KeyedCollection row is the field FluentValidation's position names, as
    // a List<T> row is: the emptied row shows its message and the invalid class, and the row
    // given a passing value keeps the valid class, before the submit and after it. A
    // string-keyed collection binds by position with model.Codes[i], because its key indexer
    // takes a string; an int-keyed one needs the cast to IList<string>, because model.Codes[i]
    // picks the key indexer. A pin of behaviour the guard must not take away. Mutation that must
    // break it: widen the KeyedCollection guard to every row of one, and the passing row loses
    // the valid class.
    [Theory]
    [InlineData("StringKeys")]
    [InlineData("IntKeysThroughTheListCast")]
    [InlineData("List")]
    public async Task A_KeyedCollection_bound_by_position_keeps_its_message_and_green(string collection)
    {
        var invalid = 0;
        Rows rows;
        Func<int, FieldIdentifier> field;
        switch (collection)
        {
            case "StringKeys":
            {
                var model = new CodesModel<StringKeyedCodes> { Codes = ["b:ok", "a:ok"] };
                rows = RenderCodes(model, [("0", () => model.Codes[0], v => ((IList<string>)model.Codes)[0] = v ?? string.Empty), ("1", () => model.Codes[1], v => ((IList<string>)model.Codes)[1] = v ?? string.Empty)], () => invalid++);
                field = index => FieldIdentifier.Create(() => model.Codes[index]);
                break;
            }

            case "IntKeysThroughTheListCast":
            {
                var model = new CodesModel<IntKeyedCodes> { Codes = ["1:ok", "0:ok"] };
                rows = RenderCodes(model, [("0", () => ((IList<string>)model.Codes)[0], v => ((IList<string>)model.Codes)[0] = v ?? string.Empty), ("1", () => ((IList<string>)model.Codes)[1], v => ((IList<string>)model.Codes)[1] = v ?? string.Empty)], () => invalid++);
                field = index => FieldIdentifier.Create(() => ((IList<string>)model.Codes)[index]);
                break;
            }

            default:
            {
                var model = new CodesModel<List<string>> { Codes = ["1:ok", "0:ok"] };
                rows = RenderCodes(model, [("0", () => model.Codes[0], v => model.Codes[0] = v ?? string.Empty), ("1", () => model.Codes[1], v => model.Codes[1] = v ?? string.Empty)], () => invalid++);
                field = index => FieldIdentifier.Create(() => model.Codes[index]);
                break;
            }
        }

        Assert.Equal("0", field(0).FieldName);
        var first = rows.Input("0").GetAttribute("value")!;
        var second = rows.Input("1").GetAttribute("value")!;

        await rows.Cut.InvokeAsync(() => rows.Input("1").Change(second[..second.IndexOf(':', StringComparison.Ordinal)] + ":fine"));
        Settle(rows);
        await rows.Cut.InvokeAsync(() => rows.Input("0").Change(first[..(first.IndexOf(':', StringComparison.Ordinal) + 1)]));
        Settle(rows);

        Assert.Equal([CodeNeedsValue], rows.Messages("0"));
        Assert.Contains("formidable-invalid", rows.Class("0").Split(' '));
        Assert.Contains("formidable-valid", rows.Class("1").Split(' '));

        await SubmitAsync(rows);

        Assert.Equal(1, invalid);
        Assert.Equal([CodeNeedsValue], rows.Messages("0"));
        Assert.Contains("formidable-invalid", rows.Class("0").Split(' '));
        Assert.False(rows.Engine.GetFieldState(field(0)).WouldPassSubmit);
        Assert.Empty(rows.Messages("1"));
        Assert.Contains("formidable-valid", rows.Class("1").Split(' '));
        Assert.True(rows.Engine.GetFieldState(field(1)).WouldPassSubmit);
    }

    // A property the collection's own type declares is no row. FluentValidation names its answer
    // Codes.Title, which resolves to the field Blazor builds for () => model.Codes.Title, so it
    // wears the valid class while it passes and its message while it fails, before the submit and
    // after it. The row bound by key beside it still never wears the valid class. Mutation that
    // must break it: treat every field of a KeyedCollection whose name is not an index as a row
    // bound by key, and Title never wears the valid class.
    [Fact]
    public async Task A_property_of_a_KeyedCollection_subclass_wears_the_valid_class()
    {
        var invalid = 0;
        var model = new CodesModel<TitledCodes> { Codes = ["b:ok", "a:ok"] };
        var rows = RenderCodes(
            model,
            [("Title", () => model.Codes.Title, v => model.Codes.Title = v ?? string.Empty), ("b", () => model.Codes["b"], v => ReplaceByKey(model.Codes, "b", v))],
            () => invalid++,
            new TitledCodesValidator());
        var title = FieldIdentifier.Create(() => model.Codes.Title);
        var keyBound = FieldIdentifier.Create(() => model.Codes["b"]);
        Assert.Same(model.Codes, title.Model);

        await rows.Cut.InvokeAsync(() => rows.Input("Title").Change("Seniors"));
        Settle(rows);

        Assert.Contains("formidable-valid", rows.Class("Title").Split(' '));
        Assert.True(rows.Engine.GetFieldState(title).WouldPassSubmit);

        await rows.Cut.InvokeAsync(() => rows.Input("Title").Change(string.Empty));
        Settle(rows);

        Assert.Equal([TitleNeedsValue], rows.Messages("Title"));
        Assert.DoesNotContain("formidable-valid", rows.Class("Title").Split(' '));
        Assert.False(rows.Engine.GetFieldState(title).WouldPassSubmit);

        await rows.Cut.InvokeAsync(() => rows.Input("Title").Change("Seniors"));
        Settle(rows);
        await SubmitAsync(rows);

        Assert.Equal(0, invalid);
        Assert.Contains("formidable-valid", rows.Class("Title").Split(' '));
        Assert.True(rows.Engine.GetFieldState(title).WouldPassSubmit);
        Assert.DoesNotContain("formidable-valid", rows.Class("b").Split(' '));
        Assert.False(rows.Engine.GetFieldState(keyBound).WouldPassSubmit);
    }

    private static void ReplaceByKey<TKey>(KeyedCollection<TKey, string> codes, TKey key, string? value)
        where TKey : notnull
    {
        var position = codes.IndexOf(codes[key]);
        ((IList<string>)codes)[position] = value ?? string.Empty;
    }

    /// <summary>Renders the form-level message and one kit input and message per row, each in an element marked <c>data-entry</c>.</summary>
    private Rows RenderCodes<TCodes>(
        CodesModel<TCodes> model,
        (string Label, Expression<Func<string?>> Accessor, Action<string?> Write)[] rows,
        Action onInvalid,
        IValidator<CodesModel<TCodes>>? validator = null)
        where TCodes : class, IList<string>
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<CodesModel<TCodes>>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<CodesModel<TCodes>>)new FluentValidationModelValidator<CodesModel<TCodes>>(validator ?? new CodesValidator<TCodes>()));
            builder.AddComponentParameter(3, "OnInvalidSubmit", EventCallback.Factory.Create<FormidableInvalidSubmitContext>(this, _ => onInvalid()));
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableModelMessage>(0);
                inner.CloseComponent();
                for (var i = 0; i < rows.Length; i++)
                {
                    var (label, accessor, write) = rows[i];
                    inner.OpenRegion(10 + i);
                    inner.OpenElement(0, "div");
                    inner.AddAttribute(1, "data-entry", label);
                    inner.OpenComponent<FormidableInputText>(2);
                    inner.AddComponentParameter(3, "Value", accessor.Compile()());
                    inner.AddComponentParameter(4, "ValueChanged", EventCallback.Factory.Create(this, write));
                    inner.AddComponentParameter(5, "ValueExpression", accessor);
                    inner.CloseComponent();
                    inner.OpenComponent<FormidableFieldMessage<string?>>(6);
                    inner.AddComponentParameter(7, "For", accessor);
                    inner.CloseComponent();
                    inner.CloseElement();
                    inner.CloseRegion();
                }

                inner.AddMarkupContent(100, "<button type=\"submit\">Save</button>");
            }));
            builder.CloseComponent();
        });

        return new Rows(cut, cut.FindComponent<FormidableForm<CodesModel<TCodes>>>().Instance.Engine!);
    }

    /// <summary>Waits for every check to land, lets the whole-form check a change arms run, then waits again.</summary>
    private void Settle(Rows rows)
    {
        rows.Cut.WaitForState(() => !rows.Engine.IsValidating);
        rows.Cut.InvokeAsync(() => _clock.Advance(new FormidableOptions().RefreshDebounce + TimeSpan.FromMilliseconds(1))).GetAwaiter().GetResult();
        rows.Cut.WaitForState(() => !rows.Engine.IsValidating);
    }

    /// <summary>Submits and waits for the submit's answer to render.</summary>
    private static async Task SubmitAsync(Rows rows)
    {
        await rows.Cut.InvokeAsync(() => rows.Cut.Find("form").Submit());
        rows.Cut.WaitForState(() => rows.Engine.HasSubmitted && !rows.Engine.IsValidating);
        await rows.Cut.InvokeAsync(() => { });
    }

    private sealed record Rows(IRenderedComponent<IComponent> Cut, IFormidableEngine Engine)
    {
        public AngleSharp.Dom.IElement Input(string row) => Cut.Find($"[data-entry=\"{row}\"] input");

        public string Class(string row) => Input(row).GetAttribute("class") ?? string.Empty;

        public IReadOnlyList<string> Messages(string row) =>
            Cut.FindAll($"[data-entry=\"{row}\"] li").Select(element => element.TextContent.Trim()).ToList();
    }
}
