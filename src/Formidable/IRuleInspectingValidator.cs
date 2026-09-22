namespace Formidable;

/// <summary>Reads what a <see cref="ValidationProfile"/>'s rules demand of a field, and which fields they mention, without running them; an optional capability beside <see cref="IModelValidator{TModel}"/>.</summary>
/// <typeparam name="TModel">The model type the validator accepts.</typeparam>
/// <remarks>
/// Implementing this interface is supported; a member added later carries a default
/// implementation, so an implementation written against these members keeps compiling. A
/// validator advertises the capability by implementing this interface and answering
/// <see cref="CanInspectRules"/> <see langword="true"/>; a caller tests both, never the type
/// alone, and tests <see cref="IRuleLevelValidator{TModel}"/> apart.
/// <see cref="FluentValidationModelValidator{TModel}"/> implements it;
/// <see cref="DelegatingModelValidator{TModel}"/> forwards it. Both readers answer from one
/// reading of the rules, stable for the validator's lifetime but possibly derived on every call,
/// so a caller asking per field caches.
/// </remarks>
// A member added later answers as an absent capability answers (a new tester reads false, a new
// reader reports the empty answer), which is the state every caller already handles because it is
// what CanInspectRules being false promises; an implementation that does not override the
// addition goes on claiming exactly what it claimed before.
public interface IRuleInspectingValidator<in TModel>
{
    /// <summary>Whether this validator can read its own rules; when <see langword="false"/>, <see cref="GetFieldRequirement"/> and <see cref="GetDeclaredFieldPaths"/> report the empty answer.</summary>
    /// <remarks>
    /// The tester is what tells "this validator says the field is not required" from "this
    /// validator cannot say", because the two readers return the same answer for both.
    /// </remarks>
    bool CanInspectRules { get; }

    /// <summary>Reports whether the rules <paramref name="profile"/> selects demand a value at <paramref name="fieldPath"/>: required, conditionally required, or not required.</summary>
    /// <param name="fieldPath">A path <see cref="GetDeclaredFieldPaths"/> lists (<c>Title</c>, <c>Address.City</c>, <c>Attendees[].Name</c>) or an indexed form of one (<c>Attendees[0].Name</c>, any index at any depth), answered by its template with each index read as <c>[]</c> after an exact match is tried; compared ordinally, and any other string reads <see cref="FieldRequirement.NotRequired"/>.</param>
    /// <param name="profile">The rule selection that decides the answer.</param>
    /// <returns>The demand; <see cref="FieldRequirement.NotRequired"/> when <see cref="CanInspectRules"/> is <see langword="false"/>.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset the validator never registered, where the validator verifies ruleset names (<see cref="ProfiledValidator{T}"/> does).</exception>
    /// <remarks>
    /// Only <c>NotEmpty()</c> and <c>NotNull()</c> count as presence; a predicate such as
    /// <c>Must(s =&gt; !string.IsNullOrWhiteSpace(s))</c> reads <see cref="FieldRequirement.NotRequired"/>.
    /// A demand reached only through a condition (<c>When</c>, <c>Unless</c>, their async forms, on
    /// the rule, the component or a rule above it, or a collection rule's per-row <c>Where</c>
    /// filter) is conditional, and an unconditional demand on the same field wins. A
    /// <c>RuleForEach(m =&gt; m.Tags).NotEmpty()</c> files under <c>Tags</c>.
    /// </remarks>
    FieldRequirement GetFieldRequirement(string fieldPath, ValidationProfile profile);

    /// <summary>Returns every field path the rules <paramref name="profile"/> selects mention, as declared templates with each collection index left open (<c>Attendees[].Name</c>).</summary>
    /// <param name="profile">The rule selection that decides which rules are read.</param>
    /// <returns>The declared paths, compared ordinally; empty when <see cref="CanInspectRules"/> is <see langword="false"/>.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset the validator never registered, where the validator verifies ruleset names (<see cref="ProfiledValidator{T}"/> does).</exception>
    /// <remarks>
    /// Nested templates chain (<c>Teams[].Members[].Alias</c>). Child validators
    /// (<c>SetValidator</c>, <c>ChildRules</c>, <c>Include</c>) are read under the selection
    /// FluentValidation runs them under, so a child rule the profile would not run is absent, and
    /// an <c>Include</c>d validator's rules land at the including level. A collection whose only
    /// rules live in its elements is not listed, and a model-level rule names no field.
    /// </remarks>
    IReadOnlySet<string> GetDeclaredFieldPaths(ValidationProfile profile);
}
