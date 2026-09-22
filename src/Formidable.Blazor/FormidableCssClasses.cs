namespace Formidable.Blazor;

/// <summary>The class name a field wears in each state, for a design system to map onto its own CSS; <see cref="FormidableCss.Compute"/> picks among them.</summary>
public sealed class FormidableCssClasses
{
    /// <summary>The class for a field with an error, whether or not it is touched or modified. Defaults to <c>"formidable-invalid"</c>.</summary>
    public string Invalid { get; set; } = "formidable-invalid";

    /// <summary>The class for a touched or modified field with no error whose worst issue is a warning. Defaults to <c>"formidable-warning"</c>.</summary>
    public string Warning { get; set; } = "formidable-warning";

    /// <summary>The class for a touched or modified field with no error or warning whose worst issue is an info. Defaults to <c>"formidable-info"</c>.</summary>
    public string Info { get; set; } = "formidable-info";

    /// <summary>The class for a touched or modified field that carries no issue of any severity and would pass submit (<see cref="FieldState.WouldPassSubmit"/>). Defaults to <c>"formidable-valid"</c>.</summary>
    public string Valid { get; set; } = "formidable-valid";

    /// <summary>The class appended while a check covering the field is running. Defaults to <c>"formidable-pending"</c>.</summary>
    public string Pending { get; set; } = "formidable-pending";
}
