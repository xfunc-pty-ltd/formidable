using System.Text;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Applies the configured class names to native InputBase components via the EditContext,
/// including the Pending class while the engine reports the field as validating. Every engine
/// installs one of these on its EditContext as it is built, so a form needs no wiring to get these
/// classes.
/// </summary>
public sealed class FormidableFieldCssClassProvider : FieldCssClassProvider
{
    private readonly FormidableCssClasses _options;
    private readonly IFormValidationEngine _engine;

    /// <summary>
    /// Creates a provider using the given class names, reading pending state from
    /// <paramref name="engine"/>. Construction is a consumer's business only when their own
    /// <c>EditContext.SetFieldCssClassProvider</c> call has replaced the installed one and they
    /// want Formidable's classes back, or when their own provider wants to delegate to this one:
    /// pass the form's <c>FormidableOptions.CssClasses</c> and its engine, both reachable through
    /// <see cref="FormidableFormContext.Engine"/>.
    /// </summary>
    public FormidableFieldCssClassProvider(FormidableCssClasses options, IFormValidationEngine engine)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engine);
        _options = options;
        _engine = engine;
    }

    /// <inheritdoc />
    public override string GetFieldCssClass(EditContext editContext, in FieldIdentifier fieldIdentifier)
    {
        var builder = new StringBuilder();

        if (editContext.GetValidationMessages(fieldIdentifier).Any())
        {
            builder.Append(_options.Invalid);
        }
        else if (editContext.IsModified(fieldIdentifier))
        {
            builder.Append(_options.Valid);
        }

        if (_engine.GetFieldState(fieldIdentifier).IsValidating)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(_options.Pending);
        }

        return builder.ToString();
    }
}
