namespace Formidable;

/// <summary>Lists the rules a <see cref="ValidationProfile"/> selects and runs a chosen set of them in one validator call; an optional capability beside <see cref="IModelValidator{TModel}"/>.</summary>
/// <typeparam name="TModel">The model type the validator accepts.</typeparam>
/// <remarks>
/// Implementing this interface is supported; a member added later carries a default
/// implementation, so an implementation written against these members keeps compiling. A
/// validator advertises the capability by implementing this interface and answering
/// <see cref="CanValidateByRule"/> <see langword="true"/>, as
/// <see cref="IRuleInspectingValidator{TModel}"/> has its own tester; a caller tests both at
/// each use, never the type alone, and validates the whole profile through
/// <see cref="IModelValidator{TModel}"/> when the test fails.
/// <see cref="FluentValidationModelValidator{TModel}"/> implements it; a wrapper keeps it by
/// deriving from <see cref="DelegatingModelValidator{TModel}"/>. An implementation resolves the
/// identities <see cref="ValidateRulesAsync"/> hands back by reading <see cref="RuleIdentity.Key"/>.
/// </remarks>
// There is no synchronous variant: a rule with async components could not honour one, and the
// Blazor engine that consumes this interface is async.
// A member added later answers as an absent capability answers (a new tester reads false, a new
// doer throws NotSupportedException rather than silently under-validating), so a caller routes
// around an implementation that does not override the addition exactly as it routes around
// CanValidateByRule being false.
public interface IRuleLevelValidator<in TModel>
{
    /// <summary>Whether this validator can select rules and run a chosen set of them; when <see langword="false"/>, <see cref="SelectRules"/>, <see cref="ValidateRulesAsync"/> and <see cref="GroupBySelectionClass"/> throw <see cref="NotSupportedException"/>.</summary>
    bool CanValidateByRule { get; }

    /// <summary>Returns an identity for each rule <paramref name="profile"/> selects, in declaration order: exactly the rules validating the whole profile would run.</summary>
    /// <param name="profile">The rule selection to list.</param>
    /// <returns>The identities, opaque and scoped to this validator instance.</returns>
    /// <exception cref="NotSupportedException"><see cref="CanValidateByRule"/> is <see langword="false"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset the validator never registered, where the validator verifies ruleset names (<see cref="ProfiledValidator{T}"/> does).</exception>
    /// <remarks>
    /// Selected: every rule outside any ruleset when <see cref="ValidationProfile.IncludeDefaultRules"/>
    /// is set or the profile names <c>"default"</c>; every rule whose ruleset membership meets
    /// <see cref="ValidationProfile.RuleSets"/>, compared case-insensitively; every rule under
    /// <c>"*"</c>; and an untagged <c>Include()</c> rule always, so its included rules can be
    /// filtered one by one when they run. A tagged <c>Include()</c> is admitted on membership like
    /// any other rule.
    /// </remarks>
    IReadOnlyList<RuleIdentity> SelectRules(ValidationProfile profile);

    /// <summary>Validates <paramref name="model"/> against exactly the given rules in one validator call, filtering their child rules by <paramref name="profile"/> as validating the whole profile would.</summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="profile">The rule selection whose ruleset names filter collection child rules, child validators and <c>Include()</c> internals.</param>
    /// <param name="rules">Identities from <see cref="SelectRules"/> on this same instance.</param>
    /// <param name="cancellationToken">Cancels an async rule still running.</param>
    /// <returns>The report for those rules, and whether it answers only for <paramref name="profile"/> (<see cref="RuleLevelResult.IsProfileScoped"/>).</returns>
    /// <exception cref="ArgumentException">An identity in <paramref name="rules"/> did not come from <see cref="SelectRules"/> on this instance, or is the default identity.</exception>
    /// <exception cref="NotSupportedException"><see cref="CanValidateByRule"/> is <see langword="false"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="profile"/> names a ruleset the validator never registered, where the validator verifies ruleset names (<see cref="ProfiledValidator{T}"/> does).</exception>
    /// <remarks>
    /// A call carrying the profile's whole selection reproduces the report of validating the whole
    /// profile issue for issue. Split across several calls, each report carries its own rules'
    /// issues in <see cref="SelectRules"/> order, so no issue moves and none drops. An empty set
    /// answers an empty report. Validator-level hooks such as <c>PreValidate</c> run once per call.
    /// </remarks>
    Task<RuleLevelResult> ValidateRulesAsync(TModel model, ValidationProfile profile,
        IReadOnlyList<RuleIdentity> rules, CancellationToken cancellationToken = default);

    /// <summary>Partitions <paramref name="rules"/> into groups no <see cref="ValidationProfile"/> splits: every profile selects a whole group or none of it.</summary>
    /// <param name="rules">Identities from <see cref="SelectRules"/> on this same instance.</param>
    /// <returns>Groups that together hold every identity in <paramref name="rules"/> exactly once; a caller runs the groups and nothing else.</returns>
    /// <exception cref="NotSupportedException"><see cref="CanValidateByRule"/> is <see langword="false"/>.</exception>
    /// <remarks>
    /// One group per rule is always a correct answer and costs only speed, one validation call per
    /// rule; splitting finer than selection demands stays correct because no profile splits a
    /// group of one. A rule whose scope reaches a child validator is best given a group of its own,
    /// because <see cref="RuleLevelResult.IsProfileScoped"/> is reported per call and would mark
    /// every rule grouped with it.
    /// </remarks>
    IReadOnlyList<IReadOnlyList<RuleIdentity>> GroupBySelectionClass(IReadOnlyList<RuleIdentity> rules);
}
