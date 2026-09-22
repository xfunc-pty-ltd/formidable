namespace Formidable;

/// <summary>
/// How firmly a <see cref="ValidationProfile"/>'s rules demand that a field carry a value —
/// the answer <see cref="IRuleInspectingValidator{TModel}.GetFieldRequirement"/> gives.
/// </summary>
/// <remarks>
/// Three-valued rather than a <see langword="bool"/> because a presence rule may carry a
/// condition, and a condition cannot be evaluated without a model instance: inspection can see
/// that the demand exists without being able to say whether it applies to the model in hand.
/// <para>
/// The members are declared weakest first, and that numeric order is load-bearing: where the
/// declared rules make two presence demands of one field, the greater value wins, which is what
/// puts an unconditional demand ahead of a conditional one. Position therefore has to match
/// strength: a member placed BELOW one that demands more wins over it silently, because the
/// comparison keeps the later member and knows nothing of what either one means. A member added
/// here is placed by how firmly it demands a value, not by where it reads best.
/// </para>
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
