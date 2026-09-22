using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Internal fast path for a field-scoped "is this field validating right now" read — the one
/// piece of <see cref="FieldState"/> <see cref="FormidableFieldCssClassProvider"/> needs, without
/// the severity scan the rest of <see cref="IFormValidationEngine.GetFieldState"/> does for
/// errors/warnings the provider already answers from the <c>EditContext</c> instead.
/// <see cref="FormValidationEngine{TModel}"/> implements this explicitly; any other
/// <see cref="IFormValidationEngine"/> (a test double, say) does not, so the provider falls back
/// to <see cref="IFormValidationEngine.GetFieldState"/> for it — the capability stays
/// engine-internal rather than growing the public engine contract for what only this one caller
/// wants.
/// </summary>
internal interface IValidatingFieldReader
{
    /// <summary>Whether a validation pass currently in flight covers <paramref name="field"/>.</summary>
    bool IsFieldValidating(FieldIdentifier field);
}

/// <summary>
/// Applies the configured class names to native InputBase components via the EditContext,
/// including the Pending class while the engine reports the field as validating. Every engine
/// installs one of these on its EditContext as it is built, so a form needs no wiring to get these
/// classes.
/// </summary>
public sealed class FormidableFieldCssClassProvider : FieldCssClassProvider
{
    private readonly FormidableCssClasses _classes;
    private readonly IFormValidationEngine _engine;
    private readonly IValidatingFieldReader? _validatingReader;

    /// <summary>
    /// Creates a provider using the given class names, reading pending state from
    /// <paramref name="engine"/>. Construction is a consumer's business only when their own
    /// <c>EditContext.SetFieldCssClassProvider</c> call has replaced the installed one and they
    /// want Formidable's classes back, or when their own provider wants to delegate to this one:
    /// pass the form's <c>FormidableOptions.CssClasses</c> and its engine, both reachable through
    /// <see cref="FormidableFormContext.Engine"/>.
    /// </summary>
    public FormidableFieldCssClassProvider(FormidableCssClasses classes, IFormValidationEngine engine)
    {
        ArgumentNullException.ThrowIfNull(classes);
        ArgumentNullException.ThrowIfNull(engine);
        _classes = classes;
        _engine = engine;
        _validatingReader = engine as IValidatingFieldReader;
    }

    /// <inheritdoc />
    public override string GetFieldCssClass(EditContext editContext, in FieldIdentifier fieldIdentifier)
    {
        var invalid = editContext.GetValidationMessages(fieldIdentifier).Any();
        var validWithoutError = editContext.IsModified(fieldIdentifier);
        var pending = _validatingReader is not null
            ? _validatingReader.IsFieldValidating(fieldIdentifier)
            : _engine.GetFieldState(fieldIdentifier).IsValidating;

        return FormidableCss.Assemble(invalid, validWithoutError, pending, _classes);
    }
}
