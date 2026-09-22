using System.Globalization;

namespace Formidable.Blazor;

/// <summary>The one rule that turns a <see cref="FieldState"/> into a field's state class, shared by the kit's inputs and the native-input class provider.</summary>
public static class FormidableCss
{
    /// <summary>Computes the space-joined state class string for <paramref name="state"/> from <paramref name="classes"/>.</summary>
    /// <param name="state">The field's state.</param>
    /// <param name="classes">The class names to pick among.</param>
    /// <returns>The class string; empty for a field that earns no class.</returns>
    public static string Compute(FieldState state, FormidableCssClasses classes) =>
        Assemble(
            state.HasErrors,
            state.IsTouched || state.IsModified,
            state.HasWarnings,
            state.HasInfos,
            state.WouldPassSubmit,
            state.IsValidating,
            classes);

    /// <summary>Joins the decided booleans into the class string: invalid, else nothing until touched or modified, else warning, info, or valid; pending appends.</summary>
    /// <param name="invalid">Whether the field has an error.</param>
    /// <param name="touchedOrModified">Whether the field is touched or modified.</param>
    /// <param name="hasWarnings">Whether the field has a warning.</param>
    /// <param name="hasInfos">Whether the field has an info.</param>
    /// <param name="wouldPassSubmit">Whether the field would pass submit.</param>
    /// <param name="pending">Whether a check covering the field is running.</param>
    /// <param name="classes">The class names to pick among.</param>
    /// <returns>The class string; empty when no tier applies and nothing is pending.</returns>
    // Invalid is ungated so a field the visitor must still fix never reads as merely advisory,
    // and an untouched, unmodified field earns no class whatever it carries. Valid alone also
    // asks WouldPassSubmit: green is a promise about submit, and a field whose submit rules have
    // no current answer, or an undisclosed failing one, wears no class rather than one it has not
    // earned. The advisory tiers ignore that bit: a disclosed warning or info is a fact about the
    // field whatever submit would say. Private to Compute so a kit input and
    // FormidableFieldCssClassProvider's native path join in exactly one place.
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

    /// <summary>Picks the error, warning or info string by <paramref name="severity"/>; any other severity picks the info string.</summary>
    /// <param name="severity">The severity to pick by.</param>
    /// <param name="errorClass">The string for <see cref="ValidationSeverity.Error"/>.</param>
    /// <param name="warningClass">The string for <see cref="ValidationSeverity.Warning"/>.</param>
    /// <param name="infoClass">The string for <see cref="ValidationSeverity.Info"/> and any other severity.</param>
    /// <returns>The chosen string.</returns>
    // Three caller-supplied literals rather than an allocation per render: every class the kit
    // picks by severity comes through here.
    internal static string SelectBySeverity(ValidationSeverity severity, string errorClass, string warningClass, string infoClass) =>
        severity switch
        {
            ValidationSeverity.Error => errorClass,
            ValidationSeverity.Warning => warningClass,
            _ => infoClass,
        };

    /// <summary>Joins a splatted attribute value, first, with a computed one, last, tolerating either being absent or empty.</summary>
    /// <param name="attributes">The consumer's splatted attributes, or <see langword="null"/>.</param>
    /// <param name="attributeName">The attribute to read from <paramref name="attributes"/>.</param>
    /// <param name="computed">The value the kit computed for the same attribute.</param>
    /// <returns>Both values space-joined, or whichever one is present.</returns>
    // The one merge rule behind every kit element that both accepts a splat and computes a value
    // for the same attribute (an input's state class, a message list's class, the summary
    // wrapper's class, aria-describedby). Behaviourally the framework's own splat/class merge,
    // reimplemented rather than taken as a dependency on an internal type.
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
    /// <param name="additionalAttributes">The consumer's splatted attributes, or <see langword="null"/>.</param>
    /// <param name="computed">The class string the kit computed.</param>
    /// <returns>Both class strings space-joined, or whichever one is present.</returns>
    internal static string CombineClassNames(IReadOnlyDictionary<string, object>? additionalAttributes, string computed) =>
        CombineSplatted(additionalAttributes, "class", computed);
}
