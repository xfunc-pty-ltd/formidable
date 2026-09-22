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

        var profile = ValidationProfile.Named($"{submit.Name}.LiveDelta", includeDefaultRules, remainingRuleSets);
        return new ProfileDeltaResult(ProfileDeltaKind.Delta, profile);
    }
}
