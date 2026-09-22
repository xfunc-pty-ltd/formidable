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

    /// <summary>Whether a check with this field in its scope is running: a live check for a change or loaded value of it, a submit, or the whole-form re-check an edit of it started after a submit.</summary>
    /// <remarks>
    /// <see cref="IFormidableEngine.DiscloseLoadedValuesAsync"/> covers no field while it runs;
    /// only <see cref="IFormidableEngine.IsValidating"/> reports it, for a page-level spinner.
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
    /// reads <see langword="false"/> here.
    /// </remarks>
    public bool WouldPassSubmit { get; init; } = true;
}
