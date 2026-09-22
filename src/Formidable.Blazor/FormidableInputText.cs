using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>A validated <c>&lt;input&gt;</c> for a <see cref="string"/> field, and the reference control built on <see cref="FormidableInputBase{TValue}"/>: the element, its value, and the base's two calls.</summary>
public sealed class FormidableInputText : FormidableInputBase<string?>
{
    /// <summary>Renders the <c>&lt;input&gt;</c>: the shared attributes, the <c>value</c>, then the commit binding.</summary>
    /// <param name="builder">The render tree builder.</param>
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        AddCommonAttributes(builder, 1);
        builder.AddAttribute(5, "value", Value);
        AddValueBinding(builder, 6);
        builder.CloseElement();
    }
}
