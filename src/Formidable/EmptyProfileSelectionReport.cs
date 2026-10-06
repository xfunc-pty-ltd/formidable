using System.Collections.Concurrent;
using FluentValidation;
using FluentValidation.Internal;

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
    /// change its rules is judged once, by the first instance to meet the profile. An
    /// <c>Include</c> the profile admits counts through the included validator's own rules. One
    /// whose validator cannot be built without a model, or cannot list its rules, is not judged
    /// and counts as selecting. A selection that cannot be counted writes nothing and is not
    /// counted again.
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
            selectsAny = SelectsAnyRule(
                validator,
                ProfileRuleSelection.BuildProfileSelector(profile),
                ProfileRuleSelection.CreateSelectionContext<TModel>(),
                [validatorType]);
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

    /// <summary>Whether <paramref name="selector"/> admits a rule of <paramref name="rules"/> that checks anything: any rule but an <c>Include</c>, or an <c>Include</c> whose validator has such a rule or cannot be judged.</summary>
    /// <param name="rules">One validator's rules.</param>
    /// <param name="selector">The profile's selector, asked about an included validator's rules too.</param>
    /// <param name="selectionContext">The model-less context the selector is asked against.</param>
    /// <param name="lookedInto">The validator types already looked into, so a validator that includes its own type is looked into once.</param>
    /// <returns><see langword="true"/> when a selected rule checks anything, or might.</returns>
    // FluentValidation's selector admits an Include outside any ruleset under every named ruleset,
    // and a run hands the included validator's rules the same selector (the adaptor an Include
    // builds carries no rulesets of its own), so the Include itself says nothing about whether
    // validating checks anything; its validator's rules do. Keyed by type rather than instance: a
    // factory building the validator's own type hands back a new instance on every read, and the
    // first instance of a type answers for it, as the record above assumes.
    private static bool SelectsAnyRule(
        IEnumerable<IValidationRule> rules,
        IValidatorSelector selector,
        IValidationContext selectionContext,
        HashSet<Type> lookedInto)
    {
        foreach (var rule in rules)
        {
            if (!selector.CanExecute(rule, string.Empty, selectionContext))
            {
                continue;
            }

            if (rule is not IIncludeRule || IncludeSelectsAnyRule(rule, selector, selectionContext, lookedInto))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the validator an admitted <c>Include</c> brings in has a rule <paramref name="selector"/> admits, counting one that cannot be judged as having one.</summary>
    /// <param name="include">The admitted <c>Include</c> rule.</param>
    /// <param name="selector">The profile's selector.</param>
    /// <param name="selectionContext">The model-less context the selector is asked against.</param>
    /// <param name="lookedInto">The validator types already looked into.</param>
    /// <returns><see langword="true"/> when the included validator selects a rule that checks anything, or cannot be judged.</returns>
    // An Include judges its own model as its property, so both of its adaptor's type arguments are
    // the rule's TypeToValidate. A validator the read cannot build without a model (a factory that
    // reads its argument), or one that cannot list its rules, might check anything, so it counts
    // as selecting and the line stays unwritten. A type already looked into adds nothing.
    private static bool IncludeSelectsAnyRule(
        IValidationRule include,
        IValidatorSelector selector,
        IValidationContext selectionContext,
        HashSet<Type> lookedInto)
    {
        foreach (var component in include.Components)
        {
            var reading = ChildValidatorReader.Read(component, include.TypeToValidate, include.TypeToValidate);
            if (reading.Validator is not IEnumerable<IValidationRule> includedRules)
            {
                return true;
            }

            if (lookedInto.Add(reading.Validator.GetType())
                && SelectsAnyRule(includedRules, selector, selectionContext, lookedInto))
            {
                return true;
            }
        }

        return false;
    }
}
