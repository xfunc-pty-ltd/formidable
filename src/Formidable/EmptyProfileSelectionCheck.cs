using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentValidation;
using FluentValidation.Internal;

namespace Formidable;

/// <summary>Refuses a profile that leaves out the default rules and selects no rule of a plain validator, as a <see cref="ProfiledValidator{T}"/> refuses a ruleset name it never registered.</summary>
// A ruleset name no rule carries passes Named's guard, and FluentValidation selects nothing for
// it without complaint, so validating under such a profile would accept every value. A
// ProfiledValidator throws for the same name, checked against the names it registered. A plain
// validator registers none, so its rules' own tags are all there is to count, and an empty count
// throws the same exception type.
internal static class EmptyProfileSelectionCheck
{
    // Weak on the validator type, so a collectible load context that defines the type can unload.
    // Per type, because a scoped or transient validator is a new instance for each request or form
    // while its rules belong to the type. Each profile is compared by its full shape, so an equal
    // profile built afresh finds its entry. Only a profile seen to select a rule is kept: one that
    // selects nothing is counted again on every call, and every such call throws, so the record
    // can spare a validator but never refuse one. Every distinct selecting profile a caller builds
    // adds an entry for as long as the type is loaded: profiles built from request data (one per
    // tenant or query value) grow the record by one entry per distinct value.
    private static readonly ConditionalWeakTable<Type, ConcurrentDictionary<ValidationProfile, byte>> Selecting = new();

    /// <summary>The sentence for an <c>Include</c> built by a factory, which the message cannot look inside.</summary>
    private const string FactoryIncludeSentence = "An Include built by a factory cannot be read.";

    /// <summary>The sentence for an <c>Include</c> whose kind or validator the reader cannot tell.</summary>
    private const string UnrecognisedIncludeSentence = "An Include of an unrecognised kind cannot be read.";

    /// <summary>Throws when <paramref name="profile"/> selects no rule of <paramref name="validator"/> that checks anything, and the validator's class overrides neither <c>Validate</c> nor <c>ValidateAsync</c>.</summary>
    /// <typeparam name="TModel">The model type the validator accepts.</typeparam>
    /// <param name="validator">The validator whose rules are counted.</param>
    /// <param name="profile">A profile that leaves out the default rules.</param>
    /// <exception cref="InvalidOperationException">The profile selects no rule of the validator, an <c>Include</c> of a validator instance counting through that validator's own rules.</exception>
    /// <remarks>
    /// Every instance of a type shares one answer once a profile is seen to select, so a validator
    /// type whose constructor arguments change its rules is spared by the first instance that
    /// selects. An <c>Include</c> built by a factory, or whose validator cannot list its rules,
    /// counts as selecting, and so do a selection that cannot be counted and a validator whose
    /// class overrides <c>Validate</c> or <c>ValidateAsync</c>.
    /// </remarks>
    internal static void Verify<TModel>(AbstractValidator<TModel> validator, ValidationProfile profile)
    {
        var selecting = Selecting.GetOrAdd(validator.GetType(), static _ => new ConcurrentDictionary<ValidationProfile, byte>());
        if (selecting.ContainsKey(profile))
        {
            return;
        }

        bool selectsAny;
        try
        {
            // Every validation of the validator passes through one of its entry points, so an
            // override of either (one forwarding to another validator, say) may check what no rule
            // of its own declares, and its rules alone cannot say it checks nothing.
            selectsAny = EntryPointOverrides<TModel>.OverridesValidate(validator)
                || EntryPointOverrides<TModel>.OverridesValidateAsync(validator)
                || SelectsAnyRule(
                    validator,
                    ProfileRuleSelection.BuildProfileSelector(profile),
                    ProfileRuleSelection.CreateSelectionContext<TModel>(),
                    new HashSet<object>(ReferenceEqualityComparer.Instance) { validator });
        }
        catch (Exception)
        {
            // A replaced selector factory whose selector needs a model cannot answer here, and a
            // count that cannot answer never refuses a call that may validate something.
            selectsAny = true;
        }

        if (!selectsAny)
        {
            throw FormidableConfigurationError.Mark(new InvalidOperationException(EmptySelectionMessage(validator, profile)));
        }

        selecting.TryAdd(profile, 0);
    }

    /// <summary>The refusal's text: the profile, the validator, the profile's rulesets, the rulesets the validator's top-level rules carry (or "none"), and a sentence for each validator it includes.</summary>
    /// <typeparam name="TModel">The model type the validator accepts.</typeparam>
    /// <param name="validator">The validator the profile selects nothing of.</param>
    /// <param name="profile">The profile being refused.</param>
    /// <returns>The message, with each name quoted.</returns>
    // The validator's own rulesets sit beside the profile's, so a misspelt name reads next to the
    // one it meant. The included validators follow, each with the rulesets its own rules carry,
    // because a name the profile meant can live there and nowhere in the top-level list. Every
    // Include is read whether or not the profile's selector admits it, which is how a factory can
    // be named here: the selection stops at an admitted factory and counts it as selecting, so one
    // reaches the message only where the selector skipped it.
    private static string EmptySelectionMessage<TModel>(AbstractValidator<TModel> validator, ValidationProfile profile)
    {
        var ruleSets = string.Join(", ", profile.RuleSets.Select(name => $"'{name}'"));
        var included = new List<string>();
        NameIncludedValidators(validator, new HashSet<object>(ReferenceEqualityComparer.Instance) { validator }, included);

        // An identical sentence says nothing the first did not, so a validator included twice, or
        // two factories, are named once.
        return $"Profile '{profile.Name}' leaves out the default rules, and no rule of " +
            $"'{FriendlyTypeName.Of(validator.GetType())}' carries its rulesets ({ruleSets}), so validating " +
            $"under it would check nothing. Rulesets its top-level rules carry: " +
            $"{Listed(CarriedRuleSets(validator))}." +
            string.Concat(included.Distinct().Select(sentence => " " + sentence));
    }

    /// <summary>The rulesets <paramref name="rules"/> carry, quoted, once each in ordinal order.</summary>
    /// <param name="rules">One validator's rules.</param>
    /// <returns>The quoted names; empty when no rule carries a ruleset.</returns>
    private static List<string> CarriedRuleSets(IEnumerable<IValidationRule> rules) =>
        rules
            .SelectMany(rule => rule.RuleSets ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .Select(name => $"'{name}'")
            .ToList();

    /// <summary>The names joined with commas, or "none" for an empty list.</summary>
    /// <param name="names">The quoted names.</param>
    /// <returns>The text the message prints for the list.</returns>
    private static string Listed(List<string> names) => names.Count > 0 ? string.Join(", ", names) : "none";

    /// <summary>Adds to <paramref name="sentences"/> one sentence for each <c>Include</c> among <paramref name="rules"/> and, below each validator it holds, the includes that validator makes.</summary>
    /// <param name="rules">One validator's rules.</param>
    /// <param name="path">The validators on the way down to <paramref name="rules"/>, compared by reference.</param>
    /// <param name="sentences">Receives the sentences, in the order the walk meets the includes.</param>
    // The same reads as the selection walk (ChildValidatorReader, the same factory test and the
    // same guard against going round), so the message names what the count could look inside and
    // says plainly what it could not. A reading that fails is unreadable, never an exception: this
    // runs while the refusal is being raised.
    private static void NameIncludedValidators(IEnumerable<IValidationRule> rules, HashSet<object> path, List<string> sentences)
    {
        foreach (var rule in rules)
        {
            if (rule is not IIncludeRule)
            {
                continue;
            }

            foreach (var component in rule.Components)
            {
                var holdsFactory = ChildValidatorReader.HoldsFactory(component);
                if (holdsFactory is not false)
                {
                    sentences.Add(holdsFactory is true ? FactoryIncludeSentence : UnrecognisedIncludeSentence);
                    continue;
                }

                var reading = ChildValidatorReader.Read(component, rule.TypeToValidate, rule.TypeToValidate);
                if (reading.Validator is null)
                {
                    sentences.Add(UnrecognisedIncludeSentence);
                    continue;
                }

                var name = FriendlyTypeName.Of(reading.Validator.GetType());
                if (reading.Validator is not IEnumerable<IValidationRule> includedRules)
                {
                    sentences.Add($"Included validator '{name}' cannot be read.");
                    continue;
                }

                if (ChildValidatorReader.RepeatsPath(path, reading.Validator, component, rule.TypeToValidate, rule.TypeToValidate))
                {
                    continue;
                }

                sentences.Add($"Included validator '{name}' carries: {Listed(CarriedRuleSets(includedRules))}.");
                path.Add(reading.Validator);
                NameIncludedValidators(includedRules, path, sentences);
                path.Remove(reading.Validator);
            }
        }
    }

    /// <summary>Whether <paramref name="selector"/> admits a rule of <paramref name="rules"/> that checks anything: any rule but an <c>Include</c>, or an <c>Include</c> built by a factory, or whose validator has such a rule or cannot be judged.</summary>
    /// <param name="rules">One validator's rules.</param>
    /// <param name="selector">The profile's selector, asked about an included validator's rules too.</param>
    /// <param name="selectionContext">The model-less context the selector is asked against.</param>
    /// <param name="path">The validators on the way down to <paramref name="rules"/>, compared by reference.</param>
    /// <returns><see langword="true"/> when a selected rule checks anything, or might.</returns>
    // FluentValidation's selector admits an Include outside any ruleset under every named ruleset,
    // and a run hands the included validator's rules the same selector (the adaptor an Include
    // builds carries no rulesets of its own), so the Include itself says nothing about whether
    // validating checks anything; its validator's rules do.
    private static bool SelectsAnyRule(
        IEnumerable<IValidationRule> rules,
        IValidatorSelector selector,
        IValidationContext selectionContext,
        HashSet<object> path)
    {
        foreach (var rule in rules)
        {
            if (!selector.CanExecute(rule, string.Empty, selectionContext))
            {
                continue;
            }

            if (rule is not IIncludeRule || IncludeSelectsAnyRule(rule, selector, selectionContext, path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the validator an admitted <c>Include</c> brings in has a rule <paramref name="selector"/> admits, counting a factory, or a validator that cannot be judged, as having one.</summary>
    /// <param name="include">The admitted <c>Include</c> rule.</param>
    /// <param name="selector">The profile's selector.</param>
    /// <param name="selectionContext">The model-less context the selector is asked against.</param>
    /// <param name="path">The validators on the way down to <paramref name="include"/>.</param>
    /// <returns><see langword="true"/> when the included validator selects a rule that checks anything, comes from a factory, or cannot be judged.</returns>
    // An Include judges its own model as its property, so both of its adaptor's type arguments are
    // the rule's TypeToValidate. An Include built by a factory counts as selecting without being
    // read, because what it builds can depend on the model, and the validator a missing model
    // picks need not be the one a real model picks. So does one whose kind cannot be told.
    // An Include holding a validator instance is read through that validator's rules; one that
    // cannot list its rules might check anything, so it counts as selecting too. One that would go
    // round again adds nothing: the same validator on the way down (ChildValidatorReader.RepeatsPath,
    // which the rule walk stops at too). The path holds only the way down, so a sibling of a type
    // already read is read in its turn.
    private static bool IncludeSelectsAnyRule(
        IValidationRule include,
        IValidatorSelector selector,
        IValidationContext selectionContext,
        HashSet<object> path)
    {
        foreach (var component in include.Components)
        {
            if (ChildValidatorReader.HoldsFactory(component) is not false)
            {
                return true;
            }

            var reading = ChildValidatorReader.Read(component, include.TypeToValidate, include.TypeToValidate);
            if (reading.Validator is not IEnumerable<IValidationRule> includedRules)
            {
                return true;
            }

            if (ChildValidatorReader.RepeatsPath(path, reading.Validator, component, include.TypeToValidate, include.TypeToValidate))
            {
                continue;
            }

            path.Add(reading.Validator);
            var selects = SelectsAnyRule(includedRules, selector, selectionContext, path);
            path.Remove(reading.Validator);
            if (selects)
            {
                return true;
            }
        }

        return false;
    }
}
