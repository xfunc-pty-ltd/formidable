using System.Globalization;

namespace Formidable.Blazor;

/// <summary>
/// Shared field CSS class rule: errors win, ungated; touched/modified without errors is warning
/// or info by the field's remaining advisory issues, and valid only when it is also known the
/// field would pass submit; pending appends while validating.
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
            state.WouldPassSubmit,
            state.IsValidating,
            classes);

    /// <summary>
    /// Joins the already-decided booleans into a space-joined class string: invalid wins outright
    /// and ungated; touched-or-modified gates every other tier, within which warning beats info
    /// beats plain valid — a field the user must still fix never reads as merely advisory, and an
    /// untouched, unmodified field earns no class at all regardless of what it carries. Valid
    /// alone carries one further requirement, <see cref="FieldState.WouldPassSubmit"/>: green is
    /// a promise about submit, so a clean-looking field whose submit-selected rules have no
    /// current answer — or whose current answer fails it undisclosed — wears no class rather
    /// than a confirmation it has not earned. The advisory tiers ignore that bit deliberately: a
    /// disclosed warning or info is a fact about the field regardless of what submit would say.
    /// Pending appends to whichever tier (or neither) applies. Private to <see cref="Compute"/>,
    /// its one caller — a Formidable input and <see cref="FormidableFieldCssClassProvider"/>'s
    /// native-input path both build a <see cref="FieldState"/> from their own sources and hand it
    /// to <see cref="Compute"/>, so this join happens in exactly one place for both.
    /// </summary>
    private static string Assemble(
        bool invalid, bool touchedOrModified, bool hasWarnings, bool hasInfos, bool wouldPassSubmit,
        bool pending, FormidableCssClasses classes)
    {
        var baseClass = invalid
            ? classes.Invalid
            : !touchedOrModified
                ? string.Empty
                : hasWarnings
                    ? classes.Warning
                    : hasInfos
                        ? classes.Info
                        : wouldPassSubmit
                            ? classes.Valid
                            : string.Empty;

        if (!pending)
        {
            return baseClass;
        }

        return baseClass.Length == 0 ? classes.Pending : $"{baseClass} {classes.Pending}";
    }

    /// <summary>
    /// Picks one of three caller-supplied constant strings by severity — the shared shape behind
    /// every class the kit picks that way, wherever it renders one, so a caller declares its own
    /// three literals once and pays no allocation choosing among them at render time.
    /// </summary>
    internal static string SelectBySeverity(ValidationSeverity severity, string errorClass, string warningClass, string infoClass) =>
        severity switch
        {
            ValidationSeverity.Error => errorClass,
            ValidationSeverity.Warning => warningClass,
            _ => infoClass,
        };

    /// <summary>
    /// Joins a consumer-splatted attribute value (first) with a computed value (last), tolerating
    /// either being absent or empty — the one merge rule behind every kit element that both
    /// accepts a splat and computes a value of its own for the same attribute, so an input's
    /// state class, a message list's structural class, the summary wrapper's class, and the
    /// <c>aria-describedby</c> the form element and a kit input each compute answer a consumer's
    /// splatted value identically.
    /// Behaviourally equivalent to the framework's internal splat/class merge, reimplemented here
    /// rather than taken as a dependency on an internal type.
    /// </summary>
    internal static string CombineSplatted(IReadOnlyDictionary<string, object>? attributes, string attributeName, string computed)
    {
        if (attributes is null || !attributes.TryGetValue(attributeName, out var splatted))
        {
            return computed;
        }

        var splattedValue = Convert.ToString(splatted, CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(splattedValue))
        {
            return computed;
        }

        return computed.Length == 0 ? splattedValue : $"{splattedValue} {computed}";
    }

    /// <summary>The <c>class</c> case of <see cref="CombineSplatted"/>.</summary>
    internal static string CombineClassNames(IReadOnlyDictionary<string, object>? additionalAttributes, string computed) =>
        CombineSplatted(additionalAttributes, "class", computed);
}
