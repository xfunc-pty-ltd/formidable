using FluentValidation.Internal;

namespace Formidable;

/// <summary>A named selection of FluentValidation rulesets: <see cref="Draft"/> runs the default rules, <see cref="Submit"/> adds the <c>"Submit"</c> ruleset, and <see cref="Named"/> composes any.</summary>
/// <remarks>Equality is by value over the whole shape; see <see cref="Equals(ValidationProfile)"/>.</remarks>
public sealed class ValidationProfile : IEquatable<ValidationProfile>
{
    /// <summary>The ruleset name the <see cref="Submit"/> profile adds: <c>"Submit"</c>.</summary>
    public const string SubmitRuleSetName = "Submit";

    /// <summary>The profile that runs the default (unnamed) rules only.</summary>
    public static ValidationProfile Draft { get; } = new("Draft", includeDefaultRules: true);

    /// <summary>The profile that runs the default rules plus the <c>"Submit"</c> ruleset.</summary>
    public static ValidationProfile Submit { get; } = new("Submit", includeDefaultRules: true, SubmitRuleSetName);

    private ValidationProfile(string name, bool includeDefaultRules, params string[] ruleSets)
    {
        Name = name;
        IncludeDefaultRules = includeDefaultRules;
        RuleSets = [.. ruleSets];
    }

    /// <summary>The profile's name, compared case-insensitively by <see cref="Equals(ValidationProfile)"/> and <see cref="FromName"/> alike.</summary>
    public string Name { get; }

    /// <summary>Whether rules outside any ruleset run under this profile.</summary>
    public bool IncludeDefaultRules { get; }

    /// <summary>The named FluentValidation rulesets this profile includes, in declared order.</summary>
    public IReadOnlyList<string> RuleSets { get; }

    /// <summary>Creates a profile that runs the default rules when <paramref name="includeDefaultRules"/> is set, plus the named <paramref name="ruleSets"/>.</summary>
    /// <param name="name">The profile's name.</param>
    /// <param name="includeDefaultRules">Whether the default (unnamed) rules also run.</param>
    /// <param name="ruleSets">The named rulesets to include, one whole name per entry.</param>
    /// <returns>The new profile.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/>, <paramref name="ruleSets"/> or an entry in it is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank; <paramref name="includeDefaultRules"/> is <see langword="false"/> and <paramref name="ruleSets"/> is empty; or an entry is blank or joins several names with <c>,</c> or <c>;</c>.</exception>
    /// <remarks>
    /// Give each name one composition: a custom name carried as a string resolves through
    /// <see cref="FromName"/> to the default rules plus one ruleset of that name. FluentValidation's
    /// own <c>"*"</c> (every rule) and <c>"default"</c> (the unnamed rules) are accepted as entries.
    /// </remarks>
    /// <example>
    /// <code>ValidationProfile.Named("Approve", includeDefaultRules: false, "Approve")</code>
    /// Pass <paramref name="includeDefaultRules"/> as a named argument: the positional
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

    /// <summary>Resolves <paramref name="name"/> to <see cref="Draft"/> or <see cref="Submit"/>, case-insensitively, or to a profile running the default rules plus one ruleset of that name.</summary>
    /// <param name="name">The profile name to resolve.</param>
    /// <returns>The matching built-in profile, or a new profile shaped the way <see cref="Submit"/> is built.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank or joins several names with <c>,</c> or <c>;</c>.</exception>
    /// <remarks>The entry point for a profile carried as a string, such as the ASP.NET Core package's <c>ValidateAttribute.Profile</c>.</remarks>
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

    /// <summary>The profile as FluentValidation's ruleset-name list: <see cref="RuleSets"/> in declared order, then <c>"default"</c> when <see cref="IncludeDefaultRules"/> is set.</summary>
    /// <returns>A fresh array each call, because a selector keeps the array it is handed.</returns>
    internal string[] ToRuleSetNames()
    {
        // Every profile-shaped selection reads this one list (the name list a validation strategy
        // is given, and the list a selector is constructed from), so the routes cannot come apart
        // into two readings of one profile. A list carrying the default ruleset name selects the
        // rules that sit outside every ruleset, which is the selection
        // ValidationStrategy.IncludeRulesNotInRuleSet asks for, so a profile spelled as one name
        // list and the same profile asked for in separate calls select the same rules. The
        // wildcard "*" needs nothing of its own: it already reaches every rule, bucketed or not,
        // so the default name beside it selects nothing further.
        var names = new List<string>(RuleSets.Count + 1);
        names.AddRange(RuleSets);
        if (IncludeDefaultRules)
        {
            names.Add(RulesetValidatorSelector.DefaultRuleSetName);
        }

        return [.. names];
    }

    /// <summary>Value equality over <see cref="Name"/>, <see cref="IncludeDefaultRules"/> and <see cref="RuleSets"/> in declared order, with names compared case-insensitively.</summary>
    /// <param name="other">The profile to compare with.</param>
    /// <returns><see langword="true"/> when both profiles declare the same composition.</returns>
    /// <remarks>
    /// Names match the way <see cref="FromName"/> and FluentValidation's own ruleset selection
    /// match them. Two profiles listing the same rulesets in a different order are unequal:
    /// equality reads the declared composition, not the selection it produces.
    /// </remarks>
    public bool Equals(ValidationProfile? other) =>
        other is not null
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
        && IncludeDefaultRules == other.IncludeDefaultRules
        && RuleSets.SequenceEqual(other.RuleSets, StringComparer.OrdinalIgnoreCase);

    /// <summary>Value equality with <paramref name="obj"/> when it is a <see cref="ValidationProfile"/>.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is a profile declaring the same composition.</returns>
    public override bool Equals(object? obj) => Equals(obj as ValidationProfile);

    /// <summary>Hashes the shape <see cref="Equals(ValidationProfile)"/> compares, so equal profiles hash equal.</summary>
    /// <returns>The hash code.</returns>
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

    /// <summary>The profile's <see cref="Name"/>.</summary>
    /// <returns>The profile's name.</returns>
    public override string ToString() => Name;
}
