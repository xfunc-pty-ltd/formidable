using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>The per-field reads <see cref="FormidableFieldCssClassProvider"/> and the message components take from the shipped engine; any other <see cref="IFormidableEngine"/> does not implement it, and each reader falls back.</summary>
// Internal so the public IFormidableEngine contract does not grow for what only these components
// want: the provider builds the same FieldState bits GetFieldState would without paying for
// IsModified or the error scan it already gets from the EditContext, and a message list reads
// InlineMessageLive for its aria-live attribute. The provider falls back to GetFieldState, a
// message list to no aria-live attribute.
internal interface IValidatingFieldReader
{
    /// <summary>Whether a running check covers <paramref name="field"/>.</summary>
    /// <param name="field">The field.</param>
    /// <returns><see langword="true"/> while the field shows "checking".</returns>
    bool IsFieldValidating(FieldIdentifier field);

    /// <summary>Whether <paramref name="field"/> has been marked touched.</summary>
    /// <param name="field">The field.</param>
    /// <returns><see langword="true"/> once touched.</returns>
    bool IsFieldTouched(FieldIdentifier field);

    /// <summary>Whether <paramref name="field"/> has a warning and whether it has an info, read together in a single scan of its issues.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The two flags.</returns>
    (bool HasWarnings, bool HasInfos) FieldAdvisories(FieldIdentifier field);

    /// <summary>Whether a submit would pass <paramref name="field"/>, the <see cref="FieldState.WouldPassSubmit"/> flag the valid class requires, without building a whole <see cref="FieldState"/>.</summary>
    /// <param name="field">The field.</param>
    /// <returns><see langword="true"/> when every submit rule for the field has answered clean.</returns>
    bool WouldPassSubmit(FieldIdentifier field);

    /// <summary>The configured <see cref="FormidableOptions.InlineMessageLive"/>, or <see langword="null"/>.</summary>
    string? InlineMessageLive { get; }
}

/// <summary>Gives a native <c>InputBase</c> component the same state classes a kit input wears, through the <c>EditContext</c>, computed by <see cref="FormidableCss.Compute"/>; the shipped engine installs one on its <c>EditContext</c> as it is built.</summary>
public sealed class FormidableFieldCssClassProvider : FieldCssClassProvider
{
    private readonly IFormidableEngine _engine;
    private readonly IValidatingFieldReader? _reader;

    /// <summary>Creates a provider that reads field state and the class names from <paramref name="engine"/>, for a page that replaced the installed provider or delegates to it from its own.</summary>
    /// <param name="engine">The engine, reachable through <see cref="FormidableFormContext.Engine"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The class names are always the engine's <see cref="FormidableOptions.CssClasses"/>; a
    /// different map is a provider of your own over <see cref="FormidableCss.Compute"/> and
    /// <see cref="IFormidableEngine.GetFieldState"/>.
    /// </remarks>
    // No overload takes a different class set: a form whose native inputs answered with names its
    // kit inputs did not would be reporting the same field state two ways.
    public FormidableFieldCssClassProvider(IFormidableEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _reader = engine as IValidatingFieldReader;
    }

    /// <summary>Computes the class string for <paramref name="fieldIdentifier"/> from the engine's state for it and the modified and error state <paramref name="editContext"/> holds.</summary>
    /// <param name="editContext">The context the native component belongs to.</param>
    /// <param name="fieldIdentifier">The field.</param>
    /// <returns>The state classes, as <see cref="FormidableCss.Compute"/> assembles them.</returns>
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

        // Read at each computation, not held from construction: the options object is what a
        // consumer reaches for to rename a class, and a provider holding the instance it was
        // built with would leave native inputs answering with the old names while kit inputs,
        // which read through the options at each render, answered with the new ones.
        return FormidableCss.Compute(state, _engine.Options.CssClasses);
    }
}
