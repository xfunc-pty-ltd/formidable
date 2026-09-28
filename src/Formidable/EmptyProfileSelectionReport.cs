using System.Collections.Concurrent;
using FluentValidation;

namespace Formidable;

/// <summary>Writes one Trace line when a profile that leaves out the default rules selects no rule of a plain validator, once per validator type and profile in a process.</summary>
// A ruleset name no rule carries passes Named's guard, and FluentValidation selects nothing for
// it without complaint, so validating under such a profile accepts every value. A
// ProfiledValidator throws for the same name. Throwing here too would refuse a call that
// succeeds, so a line in Trace is the most this can add without changing a result.
internal static class EmptyProfileSelectionReport
{
    // Keyed by validator type, because a scoped or transient validator is a new instance for each
    // request or form while its rules belong to the type. Keyed by the profile's full shape, so an
    // equal profile built afresh finds the same entry. Process-wide, so a server writes the line
    // once per run rather than once per request. Only a profile that leaves out the default rules,
    // on a plain validator, is ever recorded, but every distinct such profile a caller builds adds
    // an entry for the life of the process: profiles built from request data (one per tenant or
    // query value) grow the record by one entry per distinct value.
    private static readonly ConcurrentDictionary<(Type ValidatorType, ValidationProfile Profile), byte> Judged = new();

    /// <summary>Writes the line the first time a validator of this type meets <paramref name="profile"/>, when the profile selects none of its rules.</summary>
    /// <typeparam name="TModel">The model type the validator accepts.</typeparam>
    /// <param name="validator">The validator whose rules are counted.</param>
    /// <param name="profile">A profile that leaves out the default rules.</param>
    /// <remarks>
    /// Every instance of a type shares one answer, so a validator type whose constructor arguments
    /// change its rules is judged once, by the first instance to meet the profile. A selection that
    /// cannot be counted writes nothing and is not counted again.
    /// </remarks>
    internal static void ReportOnce<TModel>(AbstractValidator<TModel> validator, ValidationProfile profile)
    {
        var validatorType = validator.GetType();
        var key = (validatorType, profile);

        // Recorded before the count, so every later caller, and every caller racing the first,
        // stops at the lookup or the add and the line is written at most once.
        if (Judged.ContainsKey(key) || !Judged.TryAdd(key, 0))
        {
            return;
        }

        bool selectsAny;
        try
        {
            selectsAny = ProfileRuleSelection.RulesSelectedBy<TModel>(validator, profile).Any();
        }
        catch (Exception)
        {
            // A replaced selector factory whose selector needs a model cannot answer here; the
            // line stays unwritten rather than fail a call that validates correctly without it.
            return;
        }

        if (selectsAny)
        {
            return;
        }

        var ruleSets = string.Join(", ", profile.RuleSets.Select(name => $"'{name}'"));
        System.Diagnostics.Trace.WriteLine(
            $"Formidable: profile '{profile.Name}' leaves out the default rules, and its rulesets " +
            $"({ruleSets}) select no rule of '{FriendlyTypeName.Of(validatorType)}', so " +
            "validating under it checks nothing. Check each name against the validator's RuleSet " +
            "calls; a validator deriving from ProfiledValidator throws for a name it never registered.");
    }
}
