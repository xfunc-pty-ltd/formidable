namespace Formidable.Blazor;

/// <summary>Per-field state exposed to field components for CSS and pending-UI decisions.</summary>
/// <param name="IsTouched">The user has interacted with the field (marked by field components).</param>
/// <param name="IsModified">The EditContext reports the field as modified.</param>
/// <param name="IsValidating">A validation pass involving this form is in flight.</param>
/// <param name="HasErrors">The field currently has error-severity messages.</param>
/// <param name="HasWarnings">The field currently has warning-severity issues.</param>
public readonly record struct FieldState(
    bool IsTouched,
    bool IsModified,
    bool IsValidating,
    bool HasErrors,
    bool HasWarnings);
