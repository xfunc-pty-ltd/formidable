using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// An input bound to a dictionary's entry, <c>() => model.Answers[key]</c>, never wears the valid
/// class. FluentValidation names a failing entry by its position and Blazor names the input by its
/// key, so no answer can be matched to the entry, and a failing one would otherwise read as
/// passing. These pin that for each dictionary shape, kit and native inputs alike, before and after
/// a blocked submit, beside the fields that keep their green: an object property on the same form,
/// a list row bound by its index and an array element.
/// </summary>
// The guard mutation the dictionary tests name: remove the dictionary conjunct from
// SubmitCoverageTracker.WouldPassSubmit, and the cleared entry wears formidable-valid with
// WouldPassSubmit true. The controls pass under it.
public class DictionaryEntryValidityTests : BunitContext
{
    private const string AnswerRequired = "Answer required";

    private static readonly string[] Keys = ["a", "b", "c"];

    private readonly FakeTimeProvider _clock = new();

    public DictionaryEntryValidityTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddFormidableBlazor();
    }

    public sealed class Survey<TAnswers>
        where TAnswers : class
    {
        public string? Title { get; set; } = "Survey";

        public required TAnswers Answers { get; set; }
    }

    /// <summary>Answers in a class that implements only <see cref="IReadOnlyDictionary{TKey, TValue}"/>, written through a method of its own.</summary>
    public sealed class AnswerSheet : IReadOnlyDictionary<string, string?>
    {
        private readonly Dictionary<string, string?> _entries = new();

        public IEnumerable<string> Keys => _entries.Keys;

        public IEnumerable<string?> Values => _entries.Values;

        public int Count => _entries.Count;

        public string? this[string key] => _entries[key];

        public void Set(string key, string? value) => _entries[key] = value;

        public bool ContainsKey(string key) => _entries.ContainsKey(key);

        public bool TryGetValue(string key, [MaybeNullWhen(false)] out string? value) => _entries.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator() => _entries.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SurveyValidator<TAnswers> : AbstractValidator<Survey<TAnswers>>
        where TAnswers : class, IEnumerable<KeyValuePair<string, string?>>
    {
        public SurveyValidator()
        {
            RuleFor(m => m.Title).NotEmpty();
            RuleForEach(m => m.Answers).Must(kv => !string.IsNullOrEmpty(kv.Value)).WithMessage(AnswerRequired);
        }
    }

    public sealed class ListTags
    {
        public List<string?> Tags { get; set; } = ["x", "y"];
    }

    public sealed class ArrayTags
    {
        public string?[] Tags { get; set; } = ["x", "y"];
    }

    private sealed class ListTagsValidator : AbstractValidator<ListTags>
    {
        public ListTagsValidator() => RuleForEach(m => m.Tags).NotEmpty();
    }

    private sealed class ArrayTagsValidator : AbstractValidator<ArrayTags>
    {
        public ArrayTagsValidator() => RuleForEach(m => m.Tags).NotEmpty();
    }

    // Clearing entry b fails the rule, and the title's own edit runs the same check, which turns
    // the title green: the answer is current, and only the entry goes without. Mutation that must
    // break it: the guard mutation, and entry b wears formidable-valid with WouldPassSubmit true,
    // before the submit and after it.
    [Theory]
    [InlineData("Dictionary", false)]
    [InlineData("Dictionary", true)]
    [InlineData("OrderedDictionary", false)]
    [InlineData("OrderedDictionary", true)]
    [InlineData("ImmutableDictionary", false)]
    [InlineData("ImmutableDictionary", true)]
    [InlineData("ConcurrentDictionary", false)]
    [InlineData("ConcurrentDictionary", true)]
    [InlineData("SortedList", false)]
    [InlineData("SortedList", true)]
    [InlineData("ReadOnlyDictionary", false)]
    [InlineData("ReadOnlyDictionary", true)]
    [InlineData("AnswerSheet", false)]
    [InlineData("AnswerSheet", true)]
    public void A_cleared_entry_never_wears_green_before_or_after_a_blocked_submit(string shape, bool native)
    {
        var entries = RenderShape(shape, native);

        entries.Input("b").Change("");
        Settle(entries);
        entries.Input("title").Change("Survey 2");
        Settle(entries);

        Assert.Contains("formidable-valid", entries.Class("title"));
        AssertNeverGreen(entries, "b");

        Submit(entries);

        Assert.False(entries.Engine.GetFieldState(entries.Title).HasErrors);
        Assert.Contains("formidable-valid", entries.Class("title"));
        AssertNeverGreen(entries, "b");
    }

    // The guard moves the entry's border and nothing else. With TrackFormValidity on, the cleared
    // entry turns IsFormValid false, the submit is blocked on the entry's position, the summary
    // lists only the form's own line, and the warning the submit logs names the entry by that
    // position: each a pin. Mutations that must break it: the guard mutation (entry b turns green),
    // and dropping the logged warning from the engine's suppressed-issue report (no warning names
    // Answers[1]).
    [Fact]
    public void A_blocked_submit_over_a_failing_entry_names_it_by_position_in_its_warning()
    {
        var loggerProvider = new CapturingLoggerProvider();
        Services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddProvider(loggerProvider)));
        var suppressed = new List<ValidationIssue>();
        SubmitOutcome? outcome = null;
        var entries = RenderShape(
            "Dictionary",
            native: false,
            options =>
            {
                options.TrackFormValidity = true;
                options.SuppressedIssueDiagnostic = suppressed.Add;
            },
            context => outcome = context.Outcome);
        Settle(entries);
        Assert.True(entries.Engine.IsFormValid);

        entries.Input("b").Change("");
        Settle(entries);

        Assert.False(entries.Engine.IsFormValid);

        Submit(entries);

        Assert.NotNull(outcome);
        Assert.False(outcome.CanProceed);
        Assert.Equal(["Answers[1]"], outcome.Report.Issues.Select(issue => issue.Path));
        var gate = entries.Engine.Options.DefensiveGateMessage;
        Assert.Equal([gate], entries.Engine.GetVisibleIssues().Select(visible => visible.Issue.Message));
        Assert.Equal(gate, entries.Cut.Find(".formidable-summary").TextContent.Trim());
        Assert.Equal(["Answers[1]"], suppressed.Select(issue => issue.Path));
        Assert.Contains(
            loggerProvider.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("issue at 'Answers[1]' is suppressed"));
        AssertNeverGreen(entries, "b");
    }

    // A pin: green is withheld from a dictionary's entries alone. A list row bound by its index and
    // an array element keep it while their value passes, kit and native inputs alike, before and
    // after the submit. Mutation that must break it: widen the guard to every collection (any
    // IEnumerable model), and neither row turns green.
    [Theory]
    [InlineData("List", false)]
    [InlineData("List", true)]
    [InlineData("Array", false)]
    [InlineData("Array", true)]
    public void A_passing_value_keeps_green_on_a_list_row_and_an_array_element(string collection, bool native)
    {
        var rows = collection == "List" ? RenderList(native) : RenderArray(native);

        rows.Input("1").Change("z");
        Settle(rows);

        Assert.Contains("formidable-valid", rows.Class("1"));
        Assert.True(rows.Engine.GetFieldState(rows.Field("1")).WouldPassSubmit);

        Submit(rows);

        Assert.Contains("formidable-valid", rows.Class("1"));
        Assert.True(rows.Engine.GetFieldState(rows.Field("1")).WouldPassSubmit);
    }

    // Every dictionary shape reads as a dictionary, whichever of the dictionary interfaces it
    // implements, and no list, array, set or enumerable model does. Mutation that must break it:
    // read only the non-generic IDictionary, and the answer sheet and the ExpandoObject read as no
    // dictionary.
    [Fact]
    public void Every_dictionary_shape_reads_as_a_dictionary_and_no_list_does()
    {
        var sheet = new AnswerSheet();
        sheet.Set("a", "x");
        IDictionary<string, object?> expando = new ExpandoObject();
        expando["a"] = "x";
        object[] dictionaries =
        [
            new Dictionary<string, string?> { ["a"] = "x" },
            new OrderedDictionary<string, string?> { ["a"] = "x" },
            ImmutableDictionary<string, string?>.Empty.Add("a", "x"),
            new ConcurrentDictionary<string, string?> { ["a"] = "x" },
            new SortedList<string, string?> { ["a"] = "x" },
            new SortedDictionary<string, string?> { ["a"] = "x" },
            new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?> { ["a"] = "x" }),
            new Dictionary<string, string?> { ["a"] = "x" }.ToFrozenDictionary(),
            new Hashtable { ["a"] = "x" },
            sheet,
            expando,
        ];
        object[] others =
        [
            new List<string?> { "x" },
            new string?[] { "x" },
            new Collection<string?> { "x" },
            new List<KeyValuePair<string, string?>> { new("a", "x") },
            new HashSet<string> { "x" },
            new Roster(),
            new ListTags(),
            "x",
        ];

        Assert.All(dictionaries, model => Assert.True(SubmitCoverageTracker.IsDictionary(model), model.GetType().Name));
        Assert.All(others, model => Assert.False(SubmitCoverageTracker.IsDictionary(model), model.GetType().Name));
    }

    /// <summary>A model that enumerates its members' names and has a member of its own, as a view model sometimes does.</summary>
    public sealed class Roster : IEnumerable<string>
    {
        public string? Name { get; set; } = "Roster";

        public IEnumerator<string> GetEnumerator()
        {
            yield return "Name";
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Asserts that <paramref name="key"/>'s input shows no state class and no message, and that its field never would pass submit.</summary>
    private static void AssertNeverGreen(Rendered entries, string key)
    {
        Assert.DoesNotContain("formidable-valid", entries.Class(key));
        Assert.DoesNotContain("formidable-invalid", entries.Class(key));
        Assert.Empty(entries.Messages(key));
        Assert.False(entries.Engine.GetFieldState(entries.Field(key)).WouldPassSubmit);
    }

    /// <summary>Renders a survey whose answers are held in <paramref name="shape"/>, with entries a, b and c holding x, y and z.</summary>
    private Rendered RenderShape(
        string shape,
        bool native,
        Action<FormidableOptions>? configure = null,
        Action<FormidableInvalidSubmitContext>? onInvalid = null)
    {
        switch (shape)
        {
            case "Dictionary":
            {
                var model = new Survey<Dictionary<string, string?>> { Answers = new() { ["a"] = "x", ["b"] = "y", ["c"] = "z" } };
                return RenderSurvey(model, key => () => model.Answers[key], (key, value) => model.Answers[key] = value, native, configure, onInvalid);
            }

            case "OrderedDictionary":
            {
                var model = new Survey<OrderedDictionary<string, string?>> { Answers = new() { ["a"] = "x", ["b"] = "y", ["c"] = "z" } };
                return RenderSurvey(model, key => () => model.Answers[key], (key, value) => model.Answers[key] = value, native, configure, onInvalid);
            }

            case "ImmutableDictionary":
            {
                var model = new Survey<ImmutableDictionary<string, string?>>
                {
                    Answers = ImmutableDictionary<string, string?>.Empty.Add("a", "x").Add("b", "y").Add("c", "z"),
                };
                return RenderSurvey(model, key => () => model.Answers[key], (key, value) => model.Answers = model.Answers.SetItem(key, value), native, configure, onInvalid);
            }

            case "ConcurrentDictionary":
            {
                var model = new Survey<ConcurrentDictionary<string, string?>> { Answers = new() { ["a"] = "x", ["b"] = "y", ["c"] = "z" } };
                return RenderSurvey(model, key => () => model.Answers[key], (key, value) => model.Answers[key] = value, native, configure, onInvalid);
            }

            case "SortedList":
            {
                var model = new Survey<SortedList<string, string?>> { Answers = new() { ["a"] = "x", ["b"] = "y", ["c"] = "z" } };
                return RenderSurvey(model, key => () => model.Answers[key], (key, value) => model.Answers[key] = value, native, configure, onInvalid);
            }

            case "ReadOnlyDictionary":
            {
                var inner = new Dictionary<string, string?> { ["a"] = "x", ["b"] = "y", ["c"] = "z" };
                var model = new Survey<ReadOnlyDictionary<string, string?>> { Answers = new(inner) };
                return RenderSurvey(model, key => () => model.Answers[key], (key, value) => inner[key] = value, native, configure, onInvalid);
            }

            case "AnswerSheet":
            {
                var sheet = new AnswerSheet();
                sheet.Set("a", "x");
                sheet.Set("b", "y");
                sheet.Set("c", "z");
                var model = new Survey<AnswerSheet> { Answers = sheet };
                return RenderSurvey(model, key => () => model.Answers[key], (key, value) => model.Answers.Set(key, value), native, configure, onInvalid);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    /// <summary>Renders a summary, the title's input and one input per entry, each with its message, inside a <c>FormidableForm</c>.</summary>
    private Rendered RenderSurvey<TAnswers>(
        Survey<TAnswers> model,
        Func<string, Expression<Func<string?>>> entry,
        Action<string, string?> write,
        bool native,
        Action<FormidableOptions>? configure,
        Action<FormidableInvalidSubmitContext>? onInvalid)
        where TAnswers : class, IEnumerable<KeyValuePair<string, string?>>
    {
        var options = new FormidableOptions();
        configure?.Invoke(options);
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Survey<TAnswers>>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<Survey<TAnswers>>)new FluentValidationModelValidator<Survey<TAnswers>>(new SurveyValidator<TAnswers>()));
            builder.AddComponentParameter(3, "Options", options);
            if (onInvalid is not null)
            {
                builder.AddComponentParameter(4, "OnInvalidSubmit", EventCallback.Factory.Create(this, onInvalid));
            }

            builder.AddComponentParameter(5, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                inner.CloseComponent();
                RenderInput(inner, 1, "title", () => model.Title, value => model.Title = value, native);
                for (var i = 0; i < Keys.Length; i++)
                {
                    var key = Keys[i];
                    RenderInput(inner, 2 + i, key, entry(key), value => write(key, value), native);
                }

                inner.AddMarkupContent(10, "<button type=\"submit\">Save</button>");
            }));
            builder.CloseComponent();
        });

        var engine = cut.FindComponent<FormidableForm<Survey<TAnswers>>>().Instance.Engine!;
        return new Rendered(cut, engine, key => FieldIdentifier.Create(entry(key)), FieldIdentifier.Create(() => model.Title));
    }

    private Rendered RenderList(bool native)
    {
        var model = new ListTags();
        return RenderRows(model, new ListTagsValidator(), index => () => model.Tags[index], (index, value) => model.Tags[index] = value, native);
    }

    private Rendered RenderArray(bool native)
    {
        var model = new ArrayTags();
        return RenderRows(model, new ArrayTagsValidator(), index => () => model.Tags[index], (index, value) => model.Tags[index] = value, native);
    }

    /// <summary>Renders the two rows of a list or an array, each bound by its index, inside a <c>FormidableForm</c>.</summary>
    private Rendered RenderRows<TModel>(
        TModel model,
        AbstractValidator<TModel> validator,
        Func<int, Expression<Func<string?>>> row,
        Action<int, string?> write,
        bool native)
        where TModel : class
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<TModel>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<TModel>)new FluentValidationModelValidator<TModel>(validator));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                for (var i = 0; i < 2; i++)
                {
                    var index = i;
                    RenderInput(inner, index, $"{index}", row(index), value => write(index, value), native);
                }

                inner.AddMarkupContent(10, "<button type=\"submit\">Save</button>");
            }));
            builder.CloseComponent();
        });

        var engine = cut.FindComponent<FormidableForm<TModel>>().Instance.Engine!;
        return new Rendered(cut, engine, name => FieldIdentifier.Create(row(int.Parse(name, System.Globalization.CultureInfo.InvariantCulture))), default);
    }

    /// <summary>Renders one input and its message, bound to <paramref name="accessor"/>, inside an element marked <c>data-entry</c>: a kit input, or a native <c>InputText</c> with its message and anchor.</summary>
    private void RenderInput(RenderTreeBuilder builder, int region, string name, Expression<Func<string?>> accessor, Action<string?> assign, bool native)
    {
        builder.OpenRegion(region);
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "data-entry", name);

        if (native)
        {
            builder.OpenComponent<InputText>(2);
            builder.AddComponentParameter(3, "Value", accessor.Compile()());
            builder.AddComponentParameter(4, "ValueChanged", EventCallback.Factory.Create(this, assign));
            builder.AddComponentParameter(5, "ValueExpression", accessor);
            builder.CloseComponent();
            builder.OpenComponent<ValidationMessage<string?>>(6);
            builder.AddComponentParameter(7, "For", accessor);
            builder.CloseComponent();
            builder.OpenComponent<FormidableFieldAnchor<string?>>(8);
            builder.AddComponentParameter(9, "For", accessor);
            builder.CloseComponent();
        }
        else
        {
            builder.OpenComponent<FormidableInputText>(2);
            builder.AddComponentParameter(3, "Value", accessor.Compile()());
            builder.AddComponentParameter(4, "ValueChanged", EventCallback.Factory.Create(this, assign));
            builder.AddComponentParameter(5, "ValueExpression", accessor);
            builder.CloseComponent();
            builder.OpenComponent<FormidableFieldMessage<string?>>(6);
            builder.AddComponentParameter(7, "For", accessor);
            builder.CloseComponent();
        }

        builder.CloseElement();
        builder.CloseRegion();
    }

    /// <summary>Waits for every check to land, lets the whole-form check a change arms run, then waits again.</summary>
    private void Settle(Rendered rendered)
    {
        rendered.Cut.WaitForState(() => !rendered.Engine.IsValidating);
        rendered.Cut.InvokeAsync(() => _clock.Advance(new FormidableOptions().RefreshDebounce + TimeSpan.FromMilliseconds(1))).GetAwaiter().GetResult();
        rendered.Cut.WaitForState(() => !rendered.Engine.IsValidating);
    }

    /// <summary>Submits and waits for the submit's answer to render.</summary>
    private static void Submit(Rendered rendered)
    {
        rendered.Cut.Find("form").Submit();
        rendered.Cut.WaitForState(() => rendered.Engine.HasSubmitted && !rendered.Engine.IsValidating);
    }

    private sealed record Rendered(
        IRenderedComponent<IComponent> Cut,
        IFormidableEngine Engine,
        Func<string, FieldIdentifier> Field,
        FieldIdentifier Title)
    {
        public AngleSharp.Dom.IElement Input(string name) => Cut.Find($"[data-entry=\"{name}\"] input");

        public string Class(string name) => Input(name).GetAttribute("class") ?? string.Empty;

        public IReadOnlyList<string> Messages(string name) =>
            Cut.FindAll($"[data-entry=\"{name}\"] li, [data-entry=\"{name}\"] .validation-message")
                .Select(element => element.TextContent.Trim())
                .ToList();
    }
}
