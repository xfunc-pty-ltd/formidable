using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Internal fast-path reads engine-adjacent components need without growing the public
/// <see cref="IFormValidationEngine"/> contract for what only they want:
/// <see cref="FormidableFieldCssClassProvider"/> reads <see cref="IsFieldValidating"/>,
/// <see cref="IsFieldTouched"/>, <see cref="FieldAdvisories"/>, and
/// <see cref="WouldPassSubmit"/> to build the same
/// <see cref="FieldState"/> bits <see cref="IFormValidationEngine.GetFieldState"/> would, without
/// paying for <c>IsModified</c> or the error scan it already gets from the <c>EditContext</c>
/// directly; any component that renders a message list reads <see cref="InlineMessageRole"/> and
/// passes it to the shared list renderer, which adds the <c>role</c> attribute when the value is
/// not null. <see cref="FormValidationEngine{TModel}"/> implements this explicitly; any other
/// <see cref="IFormValidationEngine"/> (a test double, say) does not, so each reader falls back
/// to its own default for whichever member it needs.
/// </summary>
internal interface IValidatingFieldReader
{
    /// <summary>Whether a validation pass currently in flight covers <paramref name="field"/>.</summary>
    bool IsFieldValidating(FieldIdentifier field);

    /// <summary>Whether <paramref name="field"/> has been marked touched.</summary>
    bool IsFieldTouched(FieldIdentifier field);

    /// <summary>
    /// Whether <paramref name="field"/> currently has a warning-severity issue and whether it
    /// currently has an info-severity issue, read together in one pass over its issues rather
    /// than two separate ones.
    /// </summary>
    (bool HasWarnings, bool HasInfos) FieldAdvisories(FieldIdentifier field);

    /// <summary>
    /// Whether the engine can vouch that a submit would not fail <paramref name="field"/> — the
    /// <see cref="FieldState.WouldPassSubmit"/> conjunct the Valid class requires, answered
    /// without building the rest of a <see cref="FieldState"/>.
    /// </summary>
    bool WouldPassSubmit(FieldIdentifier field);

    /// <summary>The configured <see cref="FormidableOptions.InlineMessageRole"/>, or null.</summary>
    string? InlineMessageRole { get; }
}

/// <summary>
/// Applies the configured class names to native InputBase components via the EditContext,
/// including the Pending class while the engine reports the field as validating. Every engine
/// installs one of these on its EditContext as it is built, so a form needs no wiring to get these
/// classes. Which tier a touched-or-modified, error-free field earns — Warning, Info, or Valid —
/// is the same decision <see cref="FormidableCss.Compute"/> makes for a Formidable input, not a
/// second one that happens to agree.
/// </summary>
public sealed class FormidableFieldCssClassProvider : FieldCssClassProvider
{
    private readonly FormidableCssClasses _classes;
    private readonly IFormValidationEngine _engine;
    private readonly IValidatingFieldReader? _reader;

    /// <summary>
    /// Creates a provider using the given class names, reading touched, pending, and advisory
    /// state from <paramref name="engine"/>. Construction is a consumer's business only when their own
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
        bool touched, pending, hasWarnings, hasInfos, wouldPassSubmit;
        if (_reader is not null)
        {
            touched = _reader.IsFieldTouched(fieldIdentifier);
            pending = _reader.IsFieldValidating(fieldIdentifier);
            (hasWarnings, hasInfos) = _reader.FieldAdvisories(fieldIdentifier);
            wouldPassSubmit = _reader.WouldPassSubmit(fieldIdentifier);
        }
        else
        {
            var fallback = _engine.GetFieldState(fieldIdentifier);
            touched = fallback.IsTouched;
            pending = fallback.IsValidating;
            hasWarnings = fallback.HasWarnings;
            hasInfos = fallback.HasInfos;
            wouldPassSubmit = fallback.WouldPassSubmit;
        }

        var state = new FieldState
        {
            IsTouched = touched,
            IsModified = editContext.IsModified(fieldIdentifier),
            IsValidating = pending,
            HasErrors = editContext.GetValidationMessages(fieldIdentifier).Any(),
            HasWarnings = hasWarnings,
            HasInfos = hasInfos,
            WouldPassSubmit = wouldPassSubmit
        };

        return FormidableCss.Compute(state, _classes);
    }
}
