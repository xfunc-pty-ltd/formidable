namespace Formidable;

/// <summary>
/// How firmly a <see cref="ValidationProfile"/>'s rules demand that a field carry a value —
/// the answer <see cref="IRuleInspectingValidator{TModel}.GetFieldRequirement"/> gives.
/// </summary>
/// <remarks>
/// Three-valued rather than a <see langword="bool"/> because a presence rule may carry a
/// condition, and a condition cannot be evaluated without a model instance: inspection can see
/// that the demand exists without being able to say whether it applies to the model in hand.
/// </remarks>
public enum RuleRequirement
{
    /// <summary>
    /// No presence rule was found for the field under the profile. This is also the answer
    /// when inspection is unavailable (<see cref="IRuleInspectingValidator{TModel}.CanInspectRules"/>
    /// is <see langword="false"/>) and when presence is expressed as a predicate rather than as
    /// <c>NotEmpty()</c> or <c>NotNull()</c> — it means "not known to be required", never
    /// "proven optional", which is why a consumer-facing feature built on this must let a
    /// consumer declare requiredness itself.
    /// </summary>
    NotRequired,

    /// <summary>
    /// The profile selects a presence rule for the field, but the rule or its presence
    /// component carries a condition, so whether the demand applies depends on the model's
    /// current state. Every presence rule the profile selects for the field is conditional —
    /// one unconditional rule alongside them answers <see cref="Required"/>.
    /// </summary>
    ConditionallyRequired,

    /// <summary>
    /// The profile selects a presence rule for the field that carries no condition: every
    /// validation under this profile demands a value.
    /// </summary>
    Required,
}
