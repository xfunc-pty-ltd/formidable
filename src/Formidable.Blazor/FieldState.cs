namespace Formidable.Blazor;

/// <summary>One field's state as a component reads it: touched, modified, checking, which severities it carries, and whether it would pass submit.</summary>
/// <remarks>
/// Build one with <c>new FieldState { ... }</c>, which runs the property initializers;
/// <c>default(FieldState)</c> zeroes every member, <see cref="WouldPassSubmit"/> included. The
/// type grows by init-only properties, so code constructing it keeps compiling.
/// </remarks>
public readonly record struct FieldState
{
    /// <summary>Creates a state with every flag clear except <see cref="WouldPassSubmit"/>, which starts <see langword="true"/>.</summary>
    public FieldState()
    {
    }

    /// <summary>Whether the field has been committed, marked touched through <see cref="IFormidableEngine.MarkTouched"/> (which <see cref="FormidableFieldContext.MarkTouched"/> wraps), or adopted by <see cref="IFormidableEngine.DiscloseLoadedValuesAsync"/>.</summary>
    public bool IsTouched { get; init; }

    /// <summary>Whether the <c>EditContext</c> reports the field as modified.</summary>
    public bool IsModified { get; init; }

    /// <summary>Whether a check covering the field is running: a submit, the field's own live check, a whole-form re-check that includes it, or a check answering in place of a cancelled check of it.</summary>
    /// <remarks>
    /// A re-check includes a field whose debounced check is still to come, and the fields edited
    /// since the last re-check, during a submit or after a submit or server reply answered. After
    /// such an answer, a re-check's fields wait for the next re-check, or a live check that runs
    /// the submit profile, if a narrowed live check cancels it or a submit or load that cancelled
    /// it ends unanswered. <see cref="IFormidableEngine.DiscloseLoadedValuesAsync"/> covers fields
    /// only in its live check: those it marks touched.
    /// </remarks>
    public bool IsValidating { get; init; }

    /// <summary>Whether the field currently has an error-severity issue.</summary>
    public bool HasErrors { get; init; }

    /// <summary>Whether the field currently has a warning-severity issue.</summary>
    public bool HasWarnings { get; init; }

    /// <summary>Whether the field currently has an info-severity issue.</summary>
    public bool HasInfos { get; init; }

    /// <summary>Whether a submit would not fail this field: every submit rule has a current answer and none reports an error for it. Defaults to <see langword="true"/>.</summary>
    /// <remarks>
    /// <see cref="IFormidableEngine.IsFormValid"/> is the whole-form counterpart, kept current
    /// only under <see cref="FormidableOptions.TrackFormValidity"/>. A <c>default(FieldState)</c>
    /// reads <see langword="false"/> here. Three kinds of field always read <see langword="false"/>,
    /// because no answer can be matched to them: a dictionary's entry, which FluentValidation names
    /// by its position and never its key; an input bound by an index to a collection that is not a
    /// list; and a <see cref="System.Collections.ObjectModel.KeyedCollection{TKey, TItem}"/> row
    /// bound by a key that is not an index.
    /// </remarks>
    public bool WouldPassSubmit { get; init; } = true;
}
