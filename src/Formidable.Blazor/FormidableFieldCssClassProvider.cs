using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Internal fast-path reads two engine-adjacent components need without growing the public
/// <see cref="IFormValidationEngine"/> contract for what only they want:
/// <see cref="FormidableFieldCssClassProvider"/> reads <see cref="IsFieldValidating"/> and
/// <see cref="IsFieldTouched"/> in place of the severity scan the rest of
/// <see cref="IFormValidationEngine.GetFieldState"/> does for errors/warnings it does not need;
/// <c>FormidableMessageBase{TValue}</c> reads <see cref="InlineMessageRole"/> to decide whether
/// its rendered list carries a <c>role</c> attribute. <see cref="FormValidationEngine{TModel}"/>
/// implements this explicitly; any other <see cref="IFormValidationEngine"/> (a test double, say)
/// does not, so each reader falls back to its own default for whichever member it needs.
/// </summary>
internal interface IValidatingFieldReader
{
    /// <summary>Whether a validation pass currently in flight covers <paramref name="field"/>.</summary>
    bool IsFieldValidating(FieldIdentifier field);

    /// <summary>Whether <paramref name="field"/> has been marked touched.</summary>
    bool IsFieldTouched(FieldIdentifier field);

    /// <summary>The configured <see cref="FormidableOptions.InlineMessageRole"/>, or null.</summary>
    string? InlineMessageRole { get; }
}

/// <summary>
/// Applies the configured class names to native InputBase components via the EditContext,
/// including the Pending class while the engine reports the field as validating. Every engine
/// installs one of these on its EditContext as it is built, so a form needs no wiring to get these
/// classes. The Valid decision is the same one <see cref="FormidableCss.Compute"/> makes for a
/// Formidable input: touched or modified, with no error, earns it.
/// </summary>
public sealed class FormidableFieldCssClassProvider : FieldCssClassProvider
{
    private readonly FormidableCssClasses _classes;
    private readonly IFormValidationEngine _engine;
    private readonly IValidatingFieldReader? _reader;

    /// <summary>
    /// Creates a provider using the given class names, reading touched/pending state from
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
        _reader = engine as IValidatingFieldReader;
    }

    /// <inheritdoc />
    public override string GetFieldCssClass(EditContext editContext, in FieldIdentifier fieldIdentifier)
    {
        bool touched, pending;
        if (_reader is not null)
        {
            touched = _reader.IsFieldTouched(fieldIdentifier);
            pending = _reader.IsFieldValidating(fieldIdentifier);
        }
        else
        {
            var fallback = _engine.GetFieldState(fieldIdentifier);
            touched = fallback.IsTouched;
            pending = fallback.IsValidating;
        }

        var state = new FieldState(
            IsTouched: touched,
            IsModified: editContext.IsModified(fieldIdentifier),
            IsValidating: pending,
            HasErrors: editContext.GetValidationMessages(fieldIdentifier).Any(),
            // Compute's rule never looks at HasWarnings/HasInfos (only HasErrors and
            // IsTouched||IsModified decide Invalid/Valid), so these are placeholders, not reads --
            // a future warning/info-only class would need its own source for these bits before
            // this synthesis could feed it.
            HasWarnings: false,
            HasInfos: false);

        return FormidableCss.Compute(state, _classes);
    }
}
