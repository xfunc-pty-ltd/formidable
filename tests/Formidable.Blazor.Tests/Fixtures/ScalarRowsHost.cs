using System.Linq.Expressions;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor.Tests.Fixtures;

/// <summary>A model with a list of strings held both ways a page can hold one.</summary>
public sealed class ScalarTags
{
    public string?[] Array { get; set; } = [];

    public List<string?> List { get; set; } = [];
}

/// <summary>Each row of either list must not be empty.</summary>
public sealed class ScalarTagsValidator : AbstractValidator<ScalarTags>
{
    public ScalarTagsValidator()
    {
        RuleForEach(m => m.Array).NotEmpty();
        RuleForEach(m => m.List).NotEmpty();
    }
}

/// <summary>The kit component each row of <see cref="ScalarRowsHost"/> renders.</summary>
public enum ScalarRowComponent
{
    Input,
    Field,
    Message,
    Anchor,
}

/// <summary>How <see cref="ScalarRowsHost"/> keys its rows.</summary>
public enum ScalarRowKey
{
    /// <summary>No key anywhere: each row's component is reused by position.</summary>
    None,

    /// <summary>Each row's component keyed by the row's value.</summary>
    Value,

    /// <summary>The element around the loop keyed by the list, and the rows themselves unkeyed.</summary>
    Loop,
}

/// <summary>
/// Renders one kit component per row of <see cref="ScalarTags.Array"/> or <see cref="ScalarTags.List"/>,
/// each bound by its index as a page binds one (<c>() => Model.Array[index]</c>, with the loop
/// variable copied first), inside one element around the loop. The rows are siblings under that
/// element, so a key on a row moves it among them and a key on the element rebuilds them all.
/// </summary>
public sealed class ScalarRowsHost : ComponentBase
{
    [Parameter]
    public ScalarTags Model { get; set; } = default!;

    [Parameter]
    public bool UseArray { get; set; }

    [Parameter]
    public ScalarRowKey Key { get; set; }

    [Parameter]
    public ScalarRowComponent Component { get; set; }

    [Parameter]
    public FormidableOptions? Options { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<FormidableForm<ScalarTags>>(0);
        builder.AddComponentParameter(1, nameof(FormidableForm<ScalarTags>.Model), Model);
        builder.AddComponentParameter(2, nameof(FormidableForm<ScalarTags>.Validator), (IModelValidator<ScalarTags>)new FluentValidationModelValidator<ScalarTags>(new ScalarTagsValidator()));
        builder.AddComponentParameter(3, nameof(FormidableForm<ScalarTags>.Options), Options);
        builder.AddComponentParameter(4, nameof(FormidableForm<ScalarTags>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
        {
            IList<string?> items = UseArray ? Model.Array : Model.List;
            inner.OpenElement(0, "div");
            if (Key == ScalarRowKey.Loop)
            {
                inner.SetKey(items);
            }

            for (var i = 0; i < items.Count; i++)
            {
                var index = i;
                Expression<Func<string?>> accessor = UseArray ? () => Model.Array[index] : () => Model.List[index];
                BuildRow(inner, accessor, items[index]);
            }

            inner.CloseElement();
        }));
        builder.CloseComponent();
    }

    private void BuildRow(RenderTreeBuilder builder, Expression<Func<string?>> accessor, string? value)
    {
        switch (Component)
        {
            case ScalarRowComponent.Field:
                builder.OpenComponent<FormidableField<string?>>(10);
                KeyRow(builder, value);
                builder.AddComponentParameter(11, nameof(FormidableField<string?>.For), accessor);
                builder.AddComponentParameter(12, nameof(FormidableField<string?>.ChildContent), (RenderFragment<FormidableFieldContext>)(_ => _ => { }));
                break;

            case ScalarRowComponent.Message:
                builder.OpenComponent<FormidableFieldMessage<string?>>(20);
                KeyRow(builder, value);
                builder.AddComponentParameter(21, nameof(FormidableFieldMessage<string?>.For), accessor);
                break;

            case ScalarRowComponent.Anchor:
                builder.OpenComponent<FormidableFieldAnchor<string?>>(30);
                KeyRow(builder, value);
                builder.AddComponentParameter(31, nameof(FormidableFieldAnchor<string?>.For), accessor);
                break;

            default:
                builder.OpenComponent<FormidableInputText>(40);
                KeyRow(builder, value);
                builder.AddComponentParameter(41, nameof(FormidableInputText.Value), value);
                builder.AddComponentParameter(42, nameof(FormidableInputText.ValueExpression), accessor);
                break;
        }

        builder.CloseComponent();
    }

    private void KeyRow(RenderTreeBuilder builder, string? value)
    {
        if (Key == ScalarRowKey.Value)
        {
            builder.SetKey(value);
        }
    }
}
