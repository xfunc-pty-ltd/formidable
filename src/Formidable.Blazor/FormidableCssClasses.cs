namespace Formidable.Blazor;

/// <summary>Class names applied to a field based on its current <see cref="FieldState"/>.</summary>
public sealed class FormidableCssClasses
{
    /// <summary>Applied when the field has error-severity issues. Defaults to <c>"formidable-invalid"</c>.</summary>
    public string Invalid { get; set; } = "formidable-invalid";

    /// <summary>
    /// Applied when the field is touched or modified, has no error-severity issues, and has at
    /// least one warning-severity issue. An error-severity issue on the same field wins
    /// <see cref="Invalid"/> instead, deliberately: a field the user must still fix should never
    /// read as merely advisory. Defaults to <c>"formidable-warning"</c>.
    /// </summary>
    public string Warning { get; set; } = "formidable-warning";

    /// <summary>
    /// Applied when the field is touched or modified, has no error- or warning-severity issues,
    /// and has at least one info-severity issue. An error- or warning-severity issue on the same
    /// field wins <see cref="Invalid"/> or <see cref="Warning"/> instead, deliberately: the more
    /// urgent severity always takes the class. Defaults to <c>"formidable-info"</c>.
    /// </summary>
    public string Info { get; set; } = "formidable-info";

    /// <summary>
    /// Applied when the field is touched or modified, has no error-, warning-, or info-severity
    /// issues, and the engine can vouch that a submit would not fail it
    /// (<see cref="FieldState.WouldPassSubmit"/>): green is a promise about submit, so a field
    /// whose submit-selected rules have no answer for the value as it stands — or an answer that
    /// fails it without showing why — wears no class rather than a confirmation it has not
    /// earned. Defaults to <c>"formidable-valid"</c>.
    /// </summary>
    public string Valid { get; set; } = "formidable-valid";

    /// <summary>Appended while a validation pass involving the field is in flight. Defaults to <c>"formidable-pending"</c>.</summary>
    public string Pending { get; set; } = "formidable-pending";
}
