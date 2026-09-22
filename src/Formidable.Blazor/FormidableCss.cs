namespace Formidable.Blazor;

/// <summary>
/// Shared field CSS class rule: errors win, ungated; touched/modified without errors is warning,
/// info, or valid by the field's remaining advisory issues; pending appends while validating.
/// </summary>
public static class FormidableCss
{
    /// <summary>Computes the space-joined class string for a field state.</summary>
    public static string Compute(FieldState state, FormidableCssClasses classes) =>
        Assemble(
            state.HasErrors,
            state.IsTouched || state.IsModified,
            state.HasWarnings,
            state.HasInfos,
            state.IsValidating,
            classes);

    /// <summary>
    /// Joins the already-decided booleans into a space-joined class string: invalid wins outright
    /// and ungated; touched-or-modified gates every other tier, within which warning beats info
    /// beats plain valid — a field the user must still fix never reads as merely advisory, and an
    /// untouched, unmodified field earns no class at all regardless of what it carries. Pending
    /// appends to whichever tier (or neither) applies. Private to <see cref="Compute"/>, its one
    /// caller — a Formidable input and <see cref="FormidableFieldCssClassProvider"/>'s
    /// native-input path both build a <see cref="FieldState"/> from their own sources and hand it
    /// to <see cref="Compute"/>, so this join happens in exactly one place for both.
    /// </summary>
    private static string Assemble(
        bool invalid, bool touchedOrModified, bool hasWarnings, bool hasInfos, bool pending, FormidableCssClasses classes)
    {
        var baseClass = invalid
            ? classes.Invalid
            : !touchedOrModified
                ? string.Empty
                : hasWarnings
                    ? classes.Warning
                    : hasInfos
                        ? classes.Info
                        : classes.Valid;

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
