namespace Formidable.Blazor;

/// <summary>The outcome of subtracting a live profile from a submit profile.</summary>
internal enum ProfileDeltaKind
{
    /// <summary>The live profile selects something the submit profile does not, so subtraction is undefined.</summary>
    NotSubtractable,

    /// <summary>Subtraction is defined, but nothing remains — the submit profile selects exactly what the live profile already covers.</summary>
    Empty,

    /// <summary>Subtraction is defined and a profile remains, carrying what the submit profile runs beyond the live profile.</summary>
    Delta,
}

/// <summary>
/// The result of <see cref="ProfileDelta.Compute"/>. <see cref="Profile"/> is populated only for
/// <see cref="ProfileDeltaKind.Delta"/> — the other two outcomes carry no profile because neither
/// "nothing left to run" nor "cannot be subtracted at all" is a profile.
/// </summary>
/// <param name="Kind">Which of the three outcomes this result represents.</param>
/// <param name="Profile">The remaining profile for <see cref="ProfileDeltaKind.Delta"/>; <see langword="null"/> otherwise.</param>
internal readonly record struct ProfileDeltaResult(ProfileDeltaKind Kind, ValidationProfile? Profile);

/// <summary>
/// Works out which rules a submit profile runs that a live profile did not already run, so a
/// caller can validate only the difference on a post-submit refresh instead of re-running every
/// default rule a live pass already covered.
/// </summary>
internal static class ProfileDelta
{
    /// <summary>
    /// Subtracts <paramref name="live"/> from <paramref name="submit"/>.
    /// </summary>
    /// <param name="submit">The profile a submit (or post-submit refresh) pass runs.</param>
    /// <param name="live">The profile a live pass already ran.</param>
    /// <remarks>
    /// A profile is a pair — a default-rules flag and a set of ruleset names — so subtraction is
    /// only defined when <paramref name="live"/> selects a subset of what <paramref name="submit"/>
    /// selects: <paramref name="live"/> may not include default rules unless <paramref name="submit"/>
    /// does, and every ruleset <paramref name="live"/> names must also appear in
    /// <paramref name="submit"/>. Anything else means the live pass reached rules the submit pass
    /// does not, and silently ignoring that would drop rules from the submit verdict, so it reports
    /// <see cref="ProfileDeltaKind.NotSubtractable"/> instead of guessing.
    /// <para>
    /// Subtraction is over ruleset names, and a rule may belong to more than one ruleset. Such a
    /// rule sits in both halves of a subtraction that names one of its rulesets on each side, and
    /// runs in both — where a single unsplit run of <paramref name="submit"/> would have run it
    /// once, because FluentValidation's selector runs a rule once however many of the selected
    /// rulesets it belongs to. The combined verdict then carries its issue twice. Nothing here can
    /// detect it: a rule's ruleset membership is not visible through the
    /// <see cref="IModelValidator{TModel}"/> seam, and neither is it recorded on the issues a pass
    /// produces, so the condition is documented for the consumer who declares such a rule rather
    /// than guarded against.
    /// </para>
    /// </remarks>
    internal static ProfileDeltaResult Compute(ValidationProfile submit, ValidationProfile live)
    {
        ArgumentNullException.ThrowIfNull(submit);
        ArgumentNullException.ThrowIfNull(live);

        if (live.IncludeDefaultRules && !submit.IncludeDefaultRules)
        {
            return new ProfileDeltaResult(ProfileDeltaKind.NotSubtractable, null);
        }

        if (live.RuleSets.Except(submit.RuleSets).Any())
        {
            return new ProfileDeltaResult(ProfileDeltaKind.NotSubtractable, null);
        }

        var includeDefaultRules = submit.IncludeDefaultRules && !live.IncludeDefaultRules;
        var remainingRuleSets = submit.RuleSets.Except(live.RuleSets).ToArray();

        if (!includeDefaultRules && remainingRuleSets.Length == 0)
        {
            return new ProfileDeltaResult(ProfileDeltaKind.Empty, null);
        }

        // Both operands in the name, because a profile's name is its identity: ValidationProfile
        // equality is by name alone, so a name mentioning only the minuend would make every
        // subtraction from one submit profile equal to every other, however differently they
        // select. Deterministic in the pair, so the same subtraction always mints the same name.
        var profile = ValidationProfile.Named(
            $"{submit.Name}-{live.Name}.LiveDelta", includeDefaultRules, remainingRuleSets);
        return new ProfileDeltaResult(ProfileDeltaKind.Delta, profile);
    }
}
