using FluentValidation.Internal;

namespace Formidable;

/// <summary>
/// A named validation profile mapping to FluentValidation rulesets.
/// <see cref="Draft"/> runs the default (unnamed) rules only; <see cref="Submit"/> runs the
/// default rules plus the <c>"Submit"</c> ruleset. Custom profiles compose arbitrary ruleset
/// combinations via <see cref="Named"/>. Equality is by value over the full shape — see
/// <see cref="Equals(ValidationProfile)"/>.
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

    /// <summary>Profile name. Compared case-insensitively — by equality and by
    /// <see cref="FromName"/> alike.</summary>
    public string Name { get; }

    /// <summary>Whether rules outside any ruleset run under this profile.</summary>
    public bool IncludeDefaultRules { get; }

    /// <summary>The named FluentValidation rulesets this profile includes.</summary>
    public IReadOnlyList<string> RuleSets { get; }

    /// <summary>Creates a custom profile composing the given rulesets.</summary>
    /// <param name="name">Profile name.</param>
    /// <param name="includeDefaultRules">Whether default (unnamed) rules also run.</param>
    /// <param name="ruleSets">Named rulesets to include. At least one is required when
    /// <paramref name="includeDefaultRules"/> is <see langword="false"/> — otherwise the
    /// profile would select no rules at all. Each entry names one ruleset: blank entries and
    /// entries joining several names with <c>,</c> or <c>;</c> are rejected, because
    /// FluentValidation splits a joined name where a rule is DECLARED and never where a
    /// selection is made, so such an entry matches no ruleset and quietly selects nothing.
    /// FluentValidation's own <c>"*"</c> (every rule) and <c>"default"</c> (the unnamed rules)
    /// are names its selector honours and are accepted as they are.</param>
    /// <remarks>
    /// Give each profile name one composition app-wide. A profile carried as a string resolves
    /// through <see cref="FromName"/>, which shapes any non-built-in name the conventional way —
    /// default rules plus one ruleset named after the profile — so a differently composed
    /// profile under the same name selects different rules than its own name resolves to.
    /// </remarks>
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

        foreach (var ruleSet in ruleSets)
        {
            // The guard above refuses a profile that would select no rules; without this one
            // an entry deeper in reaches the same place — a blank name matches no ruleset, and
            // a joined one matches none either, because the selector compares whole names.
            // Both construct silently and validate nothing, while a ProfiledValidator throws
            // for the very same profile: two entry points disagreeing about one shape.
            ArgumentException.ThrowIfNullOrWhiteSpace(ruleSet, nameof(ruleSets));

            if (ruleSet.AsSpan().IndexOfAny(',', ';') >= 0)
            {
                throw new ArgumentException(
                    $"Ruleset name '{ruleSet}' joins several names. FluentValidation splits a joined name where a " +
                    "rule is declared, not where one is selected, so a profile naming it selects no rules — pass " +
                    "each name as its own argument instead.",
                    nameof(ruleSets));
            }
        }

        return new ValidationProfile(name, includeDefaultRules, ruleSets);
    }

    /// <summary>
    /// Resolves a profile from a name string: <c>"Draft"</c>/<c>"Submit"</c> match
    /// case-insensitively to the canonical <see cref="Draft"/>/<see cref="Submit"/> singletons;
    /// any other name becomes a custom profile shaped the same way <see cref="Submit"/> itself
    /// is built — default rules plus one ruleset with the same name as the profile. A blank
    /// name, or one joining several with <c>,</c> or <c>;</c>, is refused by
    /// <see cref="Named"/> rather than resolved.
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

    /// <summary>
    /// The profile as FluentValidation's own ruleset-name list: <see cref="RuleSets"/> in
    /// declared order, then <c>"default"</c> when <see cref="IncludeDefaultRules"/> is set.
    /// Every profile-shaped selection reads this one list — the name list a validation strategy
    /// is given, and the list a selector is constructed from — so the routes cannot come apart
    /// into two readings of one profile.
    /// </summary>
    /// <remarks>
    /// A list carrying the default ruleset name selects the rules that sit outside every
    /// ruleset, which is the selection <c>ValidationStrategy.IncludeRulesNotInRuleSet</c> asks
    /// for — so a profile spelled as one name list and the same profile asked for in separate
    /// calls select the same rules.
    /// The wildcard <c>"*"</c> needs nothing of its own: it already reaches every rule,
    /// bucketed or not, so the default name beside it selects nothing further. Each call
    /// answers with a fresh array, because a selector keeps the array it is handed.
    /// </remarks>
    internal string[] ToRuleSetNames()
    {
        var names = new List<string>(RuleSets.Count + 1);
        names.AddRange(RuleSets);
        if (IncludeDefaultRules)
        {
            names.Add(RulesetValidatorSelector.DefaultRuleSetName);
        }

        return [.. names];
    }

    /// <summary>
    /// Value equality over the full shape: <see cref="Name"/>, <see cref="IncludeDefaultRules"/>,
    /// and <see cref="RuleSets"/> as a sequence in declared order, with the profile name and each
    /// ruleset name compared case-insensitively — the same matching <see cref="FromName"/> and
    /// FluentValidation's own ruleset selection use. Two profiles listing the same rulesets in a
    /// different order compare unequal: equality reads the declared composition, not the rule
    /// selection it produces.
    /// </summary>
    public bool Equals(ValidationProfile? other) =>
        other is not null
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
        && IncludeDefaultRules == other.IncludeDefaultRules
        && RuleSets.SequenceEqual(other.RuleSets, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ValidationProfile);

    /// <summary>
    /// Hashes the same shape <see cref="Equals(ValidationProfile)"/> compares, so equal profiles
    /// hash equal.
    /// </summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name, StringComparer.OrdinalIgnoreCase);
        hash.Add(IncludeDefaultRules);
        foreach (var ruleSet in RuleSets)
        {
            hash.Add(ruleSet, StringComparer.OrdinalIgnoreCase);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}
