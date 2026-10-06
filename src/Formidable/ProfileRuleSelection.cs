using FluentValidation;
using FluentValidation.Internal;

namespace Formidable;

/// <summary>Which of a validator's top-level rules a profile selects, answered by the selector a full validation under that profile uses.</summary>
// Shared by the adapter's rule listing, its inspection members and the empty-selection report, so
// all three ask one selector about a validator's top-level rules. The listing returns an admitted
// Include as one rule; the report goes on to ask the same selector about the included validator's
// rules.
internal static class ProfileRuleSelection
{
    /// <summary>The rules <paramref name="profile"/> selects from <paramref name="rules"/>, in declaration order, as the profile's selector answers for each.</summary>
    /// <typeparam name="TModel">The model type the rules judge.</typeparam>
    /// <param name="rules">A validator's top-level rules.</param>
    /// <param name="profile">The rule selection to apply.</param>
    /// <returns>The selected rules, read lazily.</returns>
    internal static IEnumerable<IValidationRule> RulesSelectedBy<TModel>(IEnumerable<IValidationRule> rules, ValidationProfile profile)
    {
        var selector = BuildProfileSelector(profile);
        var selectionContext = CreateSelectionContext<TModel>();
        foreach (var rule in rules)
        {
            if (selector.CanExecute(rule, string.Empty, selectionContext))
            {
                yield return rule;
            }
        }
    }

    /// <summary>The profile's selector, built by FluentValidation's global ruleset-selector factory from <see cref="ValidationProfile.ToRuleSetNames"/>.</summary>
    /// <param name="profile">The profile to build the selector for.</param>
    /// <returns>The selector the factory returns for the profile's names.</returns>
    /// <remarks>
    /// A consumer who replaces <c>ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory</c>
    /// changes what this selector admits and what a full validation selects together.
    /// </remarks>
    // The very list ValidatorProfileExtensions names on its validation strategy, so the two
    // selections read one profile once rather than agreeing by discipline; the strategy resolves
    // its own selector through the same factory.
    internal static IValidatorSelector BuildProfileSelector(ValidationProfile profile) =>
        ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory(profile.ToRuleSetNames());

    /// <summary>A model-less context for selection questions; the stock selector reads only the rule and its own name list.</summary>
    /// <typeparam name="TModel">The model type the context is typed for.</typeparam>
    /// <returns>A context whose model is <see langword="null"/>.</returns>
    /// <remarks>
    /// A replaced selector factory (<see cref="BuildProfileSelector"/>) whose selector reads the
    /// model reads <see langword="null"/> here.
    /// </remarks>
    // FluentValidation's ruleset selector answers from the rule's memberships and its own name
    // list, using the context only as a scratchpad for bookkeeping it never reads back.
    internal static ValidationContext<TModel> CreateSelectionContext<TModel>() => new(default!);
}
