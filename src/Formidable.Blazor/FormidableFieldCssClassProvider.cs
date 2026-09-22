using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Applies the configured class names to native InputBase components via the EditContext.</summary>
public sealed class FormidableFieldCssClassProvider : FieldCssClassProvider
{
    private readonly FormidableCssOptions _options;

    /// <summary>Creates a provider using the given class names.</summary>
    public FormidableFieldCssClassProvider(FormidableCssOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public override string GetFieldCssClass(EditContext editContext, in FieldIdentifier fieldIdentifier)
    {
        if (editContext.GetValidationMessages(fieldIdentifier).Any())
        {
            return _options.Invalid;
        }

        return editContext.IsModified(fieldIdentifier) ? _options.Valid : string.Empty;
    }
}
