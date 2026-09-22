namespace Formidable.Blazor;

/// <summary>Shared field CSS class rule: errors win; touched/modified without errors is valid; pending appends while validating.</summary>
public static class FormidableCss
{
    /// <summary>Computes the space-joined class string for a field state.</summary>
    public static string Compute(FieldState state, FormidableCssClasses classes) =>
        Assemble(state.HasErrors, state.IsTouched || state.IsModified, state.IsValidating, classes);

    /// <summary>
    /// Joins the three already-decided booleans into a space-joined class string: invalid wins
    /// outright, valid applies only when not invalid, and pending appends to whichever of those
    /// (or neither) applies. Private to <see cref="Compute"/>, its one caller — a Formidable
    /// input and <see cref="FormidableFieldCssClassProvider"/>'s native-input path both build a
    /// <see cref="FieldState"/> from their own sources and hand it to <see cref="Compute"/>, so
    /// this join happens in exactly one place for both.
    /// </summary>
    private static string Assemble(bool invalid, bool validWithoutError, bool pending, FormidableCssClasses classes)
    {
        var baseClass = invalid ? classes.Invalid : validWithoutError ? classes.Valid : string.Empty;

        if (!pending)
        {
            return baseClass;
        }

        return baseClass.Length == 0 ? classes.Pending : $"{baseClass} {classes.Pending}";
    }

    /// <summary>
    /// Picks one of three caller-supplied constant strings by severity — the shared shape behind
    /// every per-issue class the kit renders (a message list item, a summary group), so a caller
    /// declares its own three literals once and pays no allocation choosing among them at render
    /// time.
    /// </summary>
    internal static string SelectBySeverity(ValidationSeverity severity, string errorClass, string warningClass, string infoClass) =>
        severity switch
        {
            ValidationSeverity.Error => errorClass,
            ValidationSeverity.Warning => warningClass,
            _ => infoClass,
        };
}
