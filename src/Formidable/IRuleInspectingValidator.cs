namespace Formidable;

/// <summary>
/// Rule-level inspection of a validator: ask what a <see cref="ValidationProfile"/>'s rules
/// demand of a field without running them. An optional capability alongside
/// <see cref="IModelValidator{TModel}"/>, implemented by
/// <see cref="FluentValidationModelValidator{TModel}"/> and sitting beside the execution
/// capability <see cref="IRuleLevelValidator{TModel}"/>. A caller's capability test is
/// <c>validator is IRuleInspectingValidator&lt;TModel&gt; inspector &amp;&amp;
/// inspector.CanInspectRules</c> — never the type test alone.
/// </summary>
/// <remarks>
/// Inspection is a weaker ask than per-rule execution and has its own gate: a validator whose
/// class-level cascade mode stops on the first failure enumerates its rules perfectly even
/// though its rules cannot be executed one at a time, so <see cref="CanInspectRules"/> is
/// <see langword="true"/> there while
/// <see cref="IRuleLevelValidator{TModel}.CanValidateByRule"/> is <see langword="false"/>.
/// <para>
/// Neither member throws when the capability is absent — an inspection answer decorates a
/// form rather than deciding a verdict, so a validator that cannot be read reports "nothing
/// found" and a caller carries on. Both members do throw where the wrapped validator verifies
/// its own ruleset names and the profile names one it never registered, exactly as validating
/// with that profile would: a typo'd name otherwise reports every field the misnamed ruleset
/// holds as not required, which is the silent under-report the ruleset check exists to prevent.
/// </para>
/// <para>
/// Answers are derived from the validator's declared rules on every call, so a caller that
/// asks per field per render caches them itself. They are stable for the lifetime of the
/// validator, since rules are declared once when it is constructed.
/// </para>
/// </remarks>
public interface IRuleInspectingValidator<in TModel>
{
    /// <summary>
    /// Whether this instance can read its own rules — the tester beside the two readers, which
    /// both report an empty answer rather than throwing when it is <see langword="false"/>.
    /// A caller checks it once to tell "this validator says the field is not required" from
    /// "this validator cannot say", because the two share an answer.
    /// </summary>
    bool CanInspectRules { get; }

    /// <summary>
    /// Reports whether the rules <paramref name="profile"/> selects demand that
    /// <paramref name="fieldPath"/> carry a value. A field is required when a selected rule for
    /// it carries an unconditional <c>NotEmpty()</c> or <c>NotNull()</c> component,
    /// conditionally required when every such component is reached only through a condition
    /// (<c>When</c>, <c>Unless</c>, or their async forms, declared on the rule or on the
    /// component), and not required otherwise. An unconditional demand wins over a conditional
    /// one on the same field.
    /// </summary>
    /// <param name="fieldPath">
    /// The property path FluentValidation gives the rule — <c>Title</c>, or <c>Address.City</c>
    /// for a nested member — compared ordinally. It is the same path
    /// <see cref="ValidationIssue.Path"/> carries for a leaf property rule.
    /// </param>
    /// <param name="profile">The profile whose rule selection decides the answer.</param>
    /// <remarks>
    /// Only presence expressed through FluentValidation's own <c>NotEmpty()</c>/<c>NotNull()</c>
    /// components is visible. Presence written as a predicate — <c>Must(s =&gt;
    /// !string.IsNullOrWhiteSpace(s))</c> — is indistinguishable from any other predicate and
    /// reports <see cref="RuleRequirement.NotRequired"/>, so a consumer-facing feature built on
    /// this offers a way to declare requiredness directly. Rules reached only through
    /// <c>Include()</c>, and rules inside a child validator, are not read: the answer covers
    /// the rules the validator declares for its own members. A rule declared with
    /// <c>RuleForEach</c> is one the validator declares for itself, filed under the
    /// collection's own path while its components judge each element — so <c>Tags</c> reports
    /// required for a <c>RuleForEach(m =&gt; m.Tags).NotEmpty()</c>, and no answer is available
    /// for the indexed path <c>Tags[0]</c> a failure would carry.
    /// </remarks>
    /// <returns>
    /// The demand, or <see cref="RuleRequirement.NotRequired"/> when
    /// <see cref="CanInspectRules"/> is <see langword="false"/>.
    /// </returns>
    RuleRequirement GetFieldRequirement(string fieldPath, ValidationProfile profile);

    /// <summary>
    /// Maps each field the rules <paramref name="profile"/> selects can produce a failure for
    /// to that field's error codes, partitioned by whether the component producing them checks
    /// for presence — the reading that tells an empty field from one holding a wrong value when
    /// only the failures are in hand.
    /// </summary>
    /// <param name="profile">The profile whose rule selection decides which components are read.</param>
    /// <remarks>
    /// Keys are the property paths the validator declares, compared ordinally, and only fields
    /// with at least one readable component appear: child-validator components are skipped,
    /// because their failures carry the child's path rather than the field's. A collection
    /// rule's own components are read and keyed under the collection's path, so a failure's
    /// indexed path (<c>Tags[0]</c>) is not itself a key — a caller matching failures to codes
    /// finds nothing for those and treats them as it treats any unmapped field. The map answers
    /// about codes, not about severity — see <see cref="FieldRuleCodes"/>.
    /// </remarks>
    /// <returns>
    /// The map, empty when <see cref="CanInspectRules"/> is <see langword="false"/>.
    /// </returns>
    IReadOnlyDictionary<string, FieldRuleCodes> GetFieldRuleCodes(ValidationProfile profile);
}
