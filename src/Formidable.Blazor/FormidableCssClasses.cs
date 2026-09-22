namespace Formidable.Blazor;

/// <summary>Class names applied to a field based on its current <see cref="FieldState"/>.</summary>
public sealed class FormidableCssClasses
{
    /// <summary>Applied when the field has error-severity issues. Defaults to <c>"formidable-invalid"</c>.</summary>
    public string Invalid { get; set; } = "formidable-invalid";

    /// <summary>Applied when the field is touched or modified and has no errors. Defaults to <c>"formidable-valid"</c>.</summary>
    public string Valid { get; set; } = "formidable-valid";

    /// <summary>Appended while a validation pass involving the field is in flight. Defaults to <c>"formidable-pending"</c>.</summary>
    public string Pending { get; set; } = "formidable-pending";
}
