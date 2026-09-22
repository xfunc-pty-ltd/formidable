using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>A validated <c>&lt;textarea&gt;</c> for a <see cref="string"/> field, identical to <see cref="FormidableInputText"/> but for the element tag.</summary>
public sealed class FormidableInputTextArea : FormidableInputBase<string?>
{
    /// <summary>Renders the <c>&lt;textarea&gt;</c>: the shared attributes, the <c>value</c>, then the commit binding.</summary>
    /// <param name="builder">The render tree builder.</param>
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "textarea");
        AddCommonAttributes(builder, 1);
        builder.AddAttribute(5, "value", Value);
        AddValueBinding(builder, 6);
        builder.CloseElement();
    }
}
