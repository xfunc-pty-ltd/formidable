namespace Formidable;

/// <summary>How firmly a <see cref="ValidationProfile"/>'s rules demand a value for a field, as <see cref="IRuleInspectingValidator{TModel}.GetFieldRequirement"/> reports it.</summary>
/// <remarks>
/// Members order by strength; where a field carries several presence rules, the strongest
/// answer wins. A presence rule may carry a condition that cannot be evaluated without a model
/// instance, which is what <see cref="ConditionallyRequired"/> reports.
/// </remarks>
// The members are declared weakest first, and that numeric order is load-bearing: where the
// declared rules make two presence demands of one field, the greater value wins, which is what
// puts an unconditional demand ahead of a conditional one. Position therefore has to match
// strength: a member placed below one that demands more wins over it silently, because the
// comparison keeps the later member and knows nothing of what either one means. A member added
// here is placed by how firmly it demands a value, not by where it reads best.
// The numeric values are not contract; only their relative order is. They are spaced so that a
// member added between two existing grades takes an unused value in between: enum members
// compile into consuming assemblies as constants, so renumbering an existing member would leave
// every already-built consumer comparing against the wrong grade until it recompiles.
public enum FieldRequirement
{
    /// <summary>No presence rule was found for the field, or none could be read: not known to be required, never proven optional.</summary>
    /// <remarks>
    /// Also the answer when <see cref="IRuleInspectingValidator{TModel}.CanInspectRules"/> is
    /// <see langword="false"/>, and when presence is expressed as a predicate rather than as
    /// <c>NotEmpty()</c> or <c>NotNull()</c>.
    /// </remarks>
    NotRequired = 0,

    /// <summary>Every presence rule the profile selects for the field is reached only through a condition, so whether the demand applies depends on the model's state.</summary>
    /// <remarks>One unconditional presence rule alongside conditional ones answers <see cref="Required"/>.</remarks>
    ConditionallyRequired = 100,

    /// <summary>The profile selects a presence rule for the field that carries no condition, so every validation under it demands a value.</summary>
    Required = 200,
}
