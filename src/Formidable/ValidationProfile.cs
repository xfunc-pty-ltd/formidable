namespace Formidable;

/// <summary>
/// A named validation profile mapping to FluentValidation rulesets.
/// <see cref="Draft"/> runs the default (unnamed) rules only; <see cref="Submit"/> runs the
/// default rules plus the <c>"Submit"</c> ruleset. Custom profiles compose arbitrary ruleset
/// combinations via <see cref="Named"/>. Equality is by <see cref="Name"/>.
/// </summary>
/// <remarks>
/// Convention: Draft rules answer "is the value malformed / out of range?" and treat default
/// values (empty string, 0, null) as valid; Submit rules answer "is the value present?" and
/// treat default values as missing. Keeping the axes orthogonal avoids double messages for a
/// single mistake.
/// </remarks>
public sealed class ValidationProfile : IEquatable<ValidationProfile>
{
    /// <summary>The ruleset name backing the <see cref="Submit"/> profile.</summary>
    public const string SubmitRuleSetName = "Submit";

    /// <summary>Default (unnamed) rules only — format, length, and range checks.</summary>
    public static ValidationProfile Draft { get; } = new("Draft", includeDefaultRules: true);

    /// <summary>Default rules plus the <c>"Submit"</c> ruleset — required-field and business rules.</summary>
    public static ValidationProfile Submit { get; } = new("Submit", includeDefaultRules: true, SubmitRuleSetName);

    private ValidationProfile(string name, bool includeDefaultRules, params string[] ruleSets)
    {
        Name = name;
        IncludeDefaultRules = includeDefaultRules;
        RuleSets = [.. ruleSets];
    }

    /// <summary>Profile name. Also the identity for equality.</summary>
    public string Name { get; }

    /// <summary>Whether rules outside any ruleset run under this profile.</summary>
    public bool IncludeDefaultRules { get; }

    /// <summary>The named FluentValidation rulesets this profile includes.</summary>
    public IReadOnlyList<string> RuleSets { get; }

    /// <summary>Creates a custom profile composing the given rulesets.</summary>
    /// <param name="name">Profile name (identity).</param>
    /// <param name="includeDefaultRules">Whether default (unnamed) rules also run.</param>
    /// <param name="ruleSets">Named rulesets to include. At least one is required when
    /// <paramref name="includeDefaultRules"/> is <see langword="false"/> — otherwise the
    /// profile would select no rules at all.</param>
    /// <example>
    /// <code>ValidationProfile.Named("Approve", includeDefaultRules: false, "Approve")</code>
    /// Pass <paramref name="includeDefaultRules"/> as a named argument — the positional
    /// <see langword="bool"/> between two strings reads opaque at the call site otherwise.
    /// </example>
    public static ValidationProfile Named(string name, bool includeDefaultRules = true, params string[] ruleSets)
    {
        ArgumentNullException.ThrowIfNull(ruleSets);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!includeDefaultRules && ruleSets.Length == 0)
        {
            throw new ArgumentException(
                "A profile must include default rules or at least one ruleset; excluding both would select no rules.",
                nameof(ruleSets));
        }

        return new ValidationProfile(name, includeDefaultRules, ruleSets);
    }

    /// <summary>
    /// Resolves a profile from a name string: <c>"Draft"</c>/<c>"Submit"</c> match
    /// case-insensitively to the canonical <see cref="Draft"/>/<see cref="Submit"/> singletons;
    /// any other name becomes a custom profile shaped the same way <see cref="Submit"/> itself
    /// is built — default rules plus one ruleset with the same name as the profile.
    /// </summary>
    /// <param name="name">The profile name to resolve.</param>
    /// <remarks>
    /// The one caller-facing entry point for a profile carried as a string, e.g. an HTTP
    /// attribute property (<c>Formidable.AspNetCore</c>'s <c>ValidateAttribute.Profile</c>) or
    /// any other string-typed configuration surface.
    /// </remarks>
    public static ValidationProfile FromName(string name)
    {
        if (string.Equals(name, "Draft", StringComparison.OrdinalIgnoreCase))
        {
            return Draft;
        }

        if (string.Equals(name, "Submit", StringComparison.OrdinalIgnoreCase))
        {
            return Submit;
        }

        return Named(name, includeDefaultRules: true, name);
    }

    /// <inheritdoc />
    public bool Equals(ValidationProfile? other) => other is not null && Name == other.Name;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ValidationProfile);

    /// <inheritdoc />
    public override int GetHashCode() => Name.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => Name;
}
