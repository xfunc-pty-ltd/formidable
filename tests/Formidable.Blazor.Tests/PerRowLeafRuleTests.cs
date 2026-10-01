using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A rule on each row of a list of strings, written <c>RuleForEach(m => m.Tags)</c> or
/// <c>RuleFor(m => m.Tags).ForEach(...)</c>, demands a value of each row and nothing of the list
/// itself. Each row's input is marked required and answered at load; the list's own component
/// is neither marked nor confirmed for it.
/// </summary>
// The mutation the RuleForEach rows here name: file a named collection rule's own components
// under its name rather than under Name[] (FluentValidationModelValidator.RulePaths), so the rule
// answers for Tags, the list's own component, and for no row. The ForEach rows pin behaviour that
// already holds, since the rule ForEach builds has no name of its own; dropping the [] such a rule
// adds breaks them.
public class PerRowLeafRuleTests : BunitContext
{
    public PerRowLeafRuleTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddFormidable();
    }

    public sealed class ListTags
    {
        public List<string?> Tags { get; set; } = [];
    }

    public sealed class ArrayTags
    {
        public string?[] Tags { get; set; } = [];
    }

    private sealed class ListEach : AbstractValidator<ListTags>
    {
        public ListEach() => RuleForEach(m => m.Tags).NotEmpty().MaximumLength(5);
    }

    private sealed class ListForEach : AbstractValidator<ListTags>
    {
        public ListForEach() => RuleFor(m => m.Tags).ForEach(t => t.NotEmpty().MaximumLength(5));
    }

    private sealed class ArrayEach : AbstractValidator<ArrayTags>
    {
        public ArrayEach() => RuleForEach(m => m.Tags).NotEmpty().MaximumLength(5);
    }

    private sealed class ArrayForEach : AbstractValidator<ArrayTags>
    {
        public ArrayForEach() => RuleFor(m => m.Tags).ForEach(t => t.NotEmpty().MaximumLength(5));
    }

    // Each row carries the mark and aria-required: a kit input renders the attribute itself, and a
    // native input renders it from the engine's answer, as a page does for one. The indicator bound
    // to the list draws nothing. Mutation that must break it: the RuleForEach mutation above, and
    // the RuleForEach rows lose both row marks while the list's indicator draws one.
    [Theory]
    [InlineData("List", "RuleForEach", false)]
    [InlineData("List", "RuleForEach", true)]
    [InlineData("Array", "RuleForEach", false)]
    [InlineData("Array", "RuleForEach", true)]
    [InlineData("List", "ForEach", false)]
    [InlineData("List", "ForEach", true)]
    [InlineData("Array", "ForEach", false)]
    [InlineData("Array", "ForEach", true)]
    public void A_per_row_rule_marks_each_row_and_not_the_list(string collection, string spelling, bool native)
    {
        var cut = collection == "List"
            ? RenderRows(new ListTags { Tags = ["", "ok"] }, m => m.Tags, spelling == "RuleForEach" ? new ListEach() : new ListForEach(), native)
            : RenderRows(new ArrayTags { Tags = ["", "ok"] }, m => m.Tags, spelling == "RuleForEach" ? new ArrayEach() : new ArrayForEach(), native);

        for (var row = 0; row < 2; row++)
        {
            Assert.Single(cut.FindAll($"[data-row=\"{row}\"] .formidable-required"));
            Assert.Equal("true", cut.Find($"[data-row=\"{row}\"] input").GetAttribute("aria-required"));
        }

        Assert.Empty(cut.FindAll("[data-list] .formidable-required"));
    }

    // A load confirms the passing row and discloses the failing one, and leaves the list's own
    // field alone: the rule demands nothing of the list, so the list is not painted valid while a
    // row fails. Mutation that must break it: the RuleForEach mutation above, and the passing row
    // stays silent while the list turns formidable-valid.
    [Theory]
    [InlineData("List")]
    [InlineData("Array")]
    public async Task A_load_under_RuleForEach_answers_each_row_and_not_the_list(string collection)
    {
        if (collection == "List")
        {
            var model = new ListTags { Tags = ["toolong", "ok"] };
            await AssertLoadAsync(model, new ListEach(), FieldIdentifier.Create(() => model.Tags[0]), FieldIdentifier.Create(() => model.Tags[1]));
        }
        else
        {
            var model = new ArrayTags { Tags = ["toolong", "ok"] };
            await AssertLoadAsync(model, new ArrayEach(), FieldIdentifier.Create(() => model.Tags[0]), FieldIdentifier.Create(() => model.Tags[1]));
        }
    }

    private static async Task AssertLoadAsync<TModel>(TModel model, AbstractValidator<TModel> validator, FieldIdentifier failing, FieldIdentifier passing)
        where TModel : class
    {
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<TModel>(
            model, editContext, new FluentValidationModelValidator<TModel>(validator),
            new ReflectionModelIntrospector(), new FormidableOptions(), new FakeTimeProvider());

        await engine.DiscloseLoadedValuesAsync();

        Assert.Contains("formidable-invalid", editContext.FieldCssClass(failing));
        Assert.Single(editContext.GetValidationMessages(failing));
        Assert.Contains("formidable-valid", editContext.FieldCssClass(passing));
        Assert.Equal(string.Empty, editContext.FieldCssClass(new FieldIdentifier(model, "Tags")));
    }

    /// <summary>Renders an indicator bound to the list, then per row an indicator and an input bound to <c>() => model.Tags[i]</c>, kit or native.</summary>
    private IRenderedComponent<IComponent> RenderRows<TModel>(
        TModel model,
        Func<TModel, IList<string?>> tags,
        AbstractValidator<TModel> validator,
        bool native)
        where TModel : class
    {
        var list = Expression.Lambda<Func<object>>(
            Expression.Convert(Expression.Property(Expression.Constant(model), "Tags"), typeof(object)));
        return Render(builder =>
        {
            builder.OpenComponent<FormidableForm<TModel>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<TModel>)new FluentValidationModelValidator<TModel>(validator));
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(context => inner =>
            {
                inner.OpenElement(0, "div");
                inner.AddAttribute(1, "data-list", true);
                inner.OpenComponent<FormidableRequiredIndicator<object>>(2);
                inner.AddComponentParameter(3, "For", list);
                inner.CloseComponent();
                inner.CloseElement();

                var items = tags(model);
                for (var i = 0; i < items.Count; i++)
                {
                    var index = i;
                    var accessor = RowAccessor(model, index);
                    inner.OpenRegion(10 + index);
                    inner.OpenElement(0, "div");
                    inner.AddAttribute(1, "data-row", index);
                    inner.OpenComponent<FormidableRequiredIndicator<string?>>(2);
                    inner.AddComponentParameter(3, "For", accessor);
                    inner.CloseComponent();
                    if (native)
                    {
                        inner.OpenComponent<InputText>(4);
                        inner.AddComponentParameter(5, "Value", items[index]);
                        inner.AddComponentParameter(6, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => items[index] = v));
                        inner.AddComponentParameter(7, "ValueExpression", accessor);
                        inner.AddAttribute(8, "aria-required",
                            context.Engine.GetFieldRequirement(FieldIdentifier.Create(accessor)) == FieldRequirement.Required ? "true" : null);
                        inner.CloseComponent();
                    }
                    else
                    {
                        inner.OpenComponent<FormidableInputText>(4);
                        inner.AddComponentParameter(5, "Value", items[index]);
                        inner.AddComponentParameter(6, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => items[index] = v));
                        inner.AddComponentParameter(7, "ValueExpression", accessor);
                        inner.CloseComponent();
                    }

                    inner.CloseElement();
                    inner.CloseRegion();
                }
            }));
            builder.CloseComponent();
        });
    }

    // The model and the index are both parameters, so the index reaches the expression as a field
    // of the one closure, the shape Blazor's FieldIdentifier.Create evaluates; an index captured
    // one scope further out is one it refuses.
    private static Expression<Func<string?>> RowAccessor(object model, int index) => model is ListTags
        ? () => ((ListTags)model).Tags[index]
        : () => ((ArrayTags)model).Tags[index];
}
