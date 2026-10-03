namespace Formidable;

/// <summary>How firmly a <see cref="ValidationProfile"/>'s rules demand a value for a field, as <see cref="IRuleInspectingValidator{TModel}.GetFieldRequirement"/> reports it.</summary>
/// <remarks>
/// Members order by strength; where a field carries several presence rules, the strongest
/// answer wins. A presence rule may carry a condition, or a severity, that cannot be evaluated
/// without a model instance, which is what <see cref="ConditionallyRequired"/> reports.
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
    /// <summary>The field carries no presence rule that fails as an error, or its rules could not be read: not known to be required, never proven optional.</summary>
    /// <remarks>
    /// Also the answer when <see cref="IRuleInspectingValidator{TModel}.CanInspectRules"/> is
    /// <see langword="false"/>, when presence is expressed as a predicate rather than as
    /// <c>NotEmpty()</c> or <c>NotNull()</c>, and when every presence rule on the field fails as
    /// a warning or info, which never blocks a submit.
    /// </remarks>
    NotRequired = 0,

    /// <summary>Whether the profile's presence rules demand a value for the field depends on the model's state: each one that can fail as an error sits under a condition or has its severity decided from the model.</summary>
    /// <remarks>One unconditional presence rule that fails as an error, alongside any of these, answers <see cref="Required"/>.</remarks>
    ConditionallyRequired = 100,

    /// <summary>The profile selects a presence rule for the field that carries no condition and fails as an error, so every validation under it demands a value.</summary>
    Required = 200,
}
