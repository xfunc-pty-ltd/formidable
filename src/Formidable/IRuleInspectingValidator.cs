namespace Formidable;

/// <summary>
/// Rule-level inspection of a validator: ask what a <see cref="ValidationProfile"/>'s rules
/// demand of a field, and which fields they speak about at all, without running them. An
/// optional capability alongside <see cref="IModelValidator{TModel}"/>, implemented by
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
/// Neither reader throws when the capability is absent — an inspection answer decorates a
/// form rather than deciding a verdict, so a validator that cannot be read reports "nothing
/// found" and a caller carries on. Both do throw where the wrapped validator verifies its own
/// ruleset names and the profile names one it never registered, exactly as validating with
/// that profile would: a typo'd name otherwise reports every field the misnamed ruleset holds
/// as not required, which is the silent under-report the ruleset check exists to prevent.
/// </para>
/// <para>
/// The two readers answer about the same set of paths — a path
/// <see cref="GetFieldRequirement"/> reports a demand for is one
/// <see cref="GetDeclaredFieldPaths"/> lists — because both derive from one walk of the
/// declared rules. They are stable for the lifetime of the validator, since rules are declared
/// once when it is constructed — but an implementation is free to derive them on every call,
/// so a caller that asks per field per render caches them itself.
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
    /// (<c>When</c>, <c>Unless</c>, or their async forms, declared on the rule, on the
    /// component, or on a rule the walk passed through to reach it), and not required
    /// otherwise. An unconditional demand wins over a conditional one on the same field.
    /// </summary>
    /// <param name="fieldPath">
    /// One of the paths <see cref="GetDeclaredFieldPaths"/> lists — <c>Title</c>,
    /// <c>Address.City</c> for a nested member, or the templated <c>Attendees[].Name</c> for a
    /// rule declared per collection element. Compared ordinally. Any other string reports
    /// <see cref="RuleRequirement.NotRequired"/>, which includes the INDEXED path a failure
    /// carries (<c>Attendees[0].Name</c>): replace an index with <c>[]</c> before asking.
    /// </param>
    /// <param name="profile">The profile whose rule selection decides the answer.</param>
    /// <remarks>
    /// Only presence expressed through FluentValidation's own <c>NotEmpty()</c>/<c>NotNull()</c>
    /// components is visible. Presence written as a predicate — <c>Must(s =&gt;
    /// !string.IsNullOrWhiteSpace(s))</c> — is indistinguishable from any other predicate and
    /// reports <see cref="RuleRequirement.NotRequired"/>, so a consumer-facing feature built on
    /// this offers a way to declare requiredness directly. A <c>RuleForEach</c> whose
    /// components judge each element directly is a rule the validator declares for itself,
    /// filed under the collection's own path — so <c>Tags</c> reports required for a
    /// <c>RuleForEach(m =&gt; m.Tags).NotEmpty()</c>.
    /// </remarks>
    /// <returns>
    /// The demand, or <see cref="RuleRequirement.NotRequired"/> when
    /// <see cref="CanInspectRules"/> is <see langword="false"/>.
    /// </returns>
    RuleRequirement GetFieldRequirement(string fieldPath, ValidationProfile profile);

    /// <summary>
    /// Every field path the rules <paramref name="profile"/> selects speak about, as the
    /// validator declares them — the answer to "which fields does this form have rules for at
    /// all", which no reading of a set of failures can give.
    /// </summary>
    /// <param name="profile">The profile whose rule selection decides which rules are read.</param>
    /// <remarks>
    /// Paths are TEMPLATES, not the paths a particular model produces: a rule declared per
    /// collection element is listed with the index left open, so
    /// <c>RuleForEach(r =&gt; r.Attendees).ChildRules(a =&gt; a.RuleFor(x =&gt; x.Name)...)</c>
    /// lists <c>Attendees[].Name</c> and never <c>Attendees[0].Name</c>. A caller matching a
    /// failure's path against this set replaces each index with <c>[]</c> first; a caller
    /// enumerating real fields expands each <c>[]</c> against the model in hand. Nested
    /// templates chain (<c>Teams[].Members[].Alias</c>), and paths are compared ordinally.
    /// <para>
    /// The walk descends into child validators — <c>SetValidator</c>, <c>ChildRules</c> and
    /// <c>Include</c> alike — and applies the profile's selection at every level, so a child
    /// rule tagged into a ruleset the profile does not name is absent exactly as a top-level
    /// one would be. An <c>Include</c>d validator's rules merge at the including validator's
    /// own level, since that is where its failures land. A collection whose only rules live in
    /// its elements is NOT listed itself — <c>Sessions[].Seats</c> without <c>Sessions</c> —
    /// because no rule speaks about the collection. A model-level rule
    /// (<c>RuleFor(x =&gt; x)</c>) names no field and reaches this answer only through whatever
    /// child validator it carries.
    /// </para>
    /// <para>
    /// Three shapes are deliberately not read, each of them silently rather than by throwing,
    /// because a missing path costs a caller a decoration while a wrong one costs it a wrong
    /// claim. A child validator supplied by a lambda
    /// (<c>SetValidator((parent, child) =&gt; ...)</c>) is resolved by running that lambda with
    /// no model, so one that reads the model it was not given is treated as unreadable and its
    /// paths are absent — a lambda that ignores its arguments answers, and is read like any
    /// other child. A validator that includes itself, directly or through a cycle, is walked
    /// once, so the paths below the repeat are absent rather than generated to some arbitrary
    /// depth. And rules declared inside <c>DependentRules</c> are not read.
    /// </para>
    /// <para>
    /// A fourth shape runs the OTHER way, and the guarantee above does not cover it.
    /// <c>SetValidator(validator, ruleSets)</c> scopes the child's rules to those rulesets, and
    /// that scoping is not applied here: the child is read whole under whatever profile reaches
    /// the rule holding it. A rule inside such a child is therefore listed — and reported as a
    /// demand by <see cref="GetFieldRequirement"/> — under a profile that does not enforce it,
    /// which is an over-claim where the three above under-claim. Scoping a child by tagging its
    /// own rules instead (a <c>RuleSet</c> block inside the child validator) is read exactly,
    /// under every profile, because the selection is then applied where the walk can see it.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The declared paths, empty when <see cref="CanInspectRules"/> is <see langword="false"/>.
    /// </returns>
    IReadOnlySet<string> GetDeclaredFieldPaths(ValidationProfile profile);
}
