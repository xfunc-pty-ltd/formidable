using FluentValidation;
using Formidable.Tests.Fixtures;

namespace Formidable.Tests;

/// <summary>
/// <see cref="DelegatingModelValidator{TModel}"/>: what a wrapper built on it keeps, what it
/// answers over a validator that has less to give, and the one obligation its own documentation
/// puts on a derived class.
/// <para>
/// Every capability assertion here goes through the INTERFACE rather than through the class, and
/// that is the whole discrimination: the members stay public on the class whether or not the
/// class still declares the interface, so an assertion reading them directly passes over a base
/// that has stopped implementing the thing under test.
/// </para>
/// </summary>
public class DelegatingModelValidatorTests
{
    /// <summary>Changes nothing — the shape that isolates what forwarding alone does.</summary>
    private sealed class PassThroughValidator(IModelValidator<TestOrder> inner)
        : DelegatingModelValidator<TestOrder>(inner);

    /// <summary>
    /// The shape the class exists for: a wrapper presenting the rules a different view of the
    /// model, written the way its documentation warns against — one validation entry point
    /// overridden and the other two left forwarding.
    /// </summary>
    private sealed class StagedAtOneEntryPoint(IModelValidator<TestOrder> inner)
        : DelegatingModelValidator<TestOrder>(inner)
    {
        public override Task<ValidationReport> ValidateAsync(
            TestOrder model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
            base.ValidateAsync(Staged(model), profile, cancellationToken);
    }

    /// <summary>The same wrapper written the way its documentation asks for.</summary>
    private sealed class StagedAtEveryEntryPoint(IModelValidator<TestOrder> inner)
        : DelegatingModelValidator<TestOrder>(inner)
    {
        public override Task<ValidationReport> ValidateAsync(
            TestOrder model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
            base.ValidateAsync(Staged(model), profile, cancellationToken);

        public override ValidationReport Validate(TestOrder model, ValidationProfile profile) =>
            base.Validate(Staged(model), profile);

        public override Task<RuleLevelResult> ValidateRulesAsync(
            TestOrder model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules,
            CancellationToken cancellationToken = default) =>
            base.ValidateRulesAsync(Staged(model), profile, rules, cancellationToken);
    }

    /// <summary>
    /// The description a submit demands, held somewhere the model does not carry it yet — the
    /// staged-value case in its smallest form. The rules see a filled description; the model the
    /// caller passed keeps its own.
    /// </summary>
    private static TestOrder Staged(TestOrder model) => new()
    {
        Description = model.Description.Length == 0 ? "staged" : model.Description,
        Customer = model.Customer,
        LineItems = model.LineItems,
        Attributes = model.Attributes,
    };

    /// <summary>
    /// A validator that implements the validation seam and nothing beside it — a hand-rolled
    /// implementation, or the very wrapper this class exists to replace. It reports no issues:
    /// what these tests read from it is the absence of the two capabilities.
    /// </summary>
    private sealed class BareValidator : IModelValidator<TestOrder>
    {
        public Task<ValidationReport> ValidateAsync(
            TestOrder model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(Validate(model, profile));

        public ValidationReport Validate(TestOrder model, ValidationProfile profile) => new([]);
    }

    /// <summary>
    /// Enumerates its rules perfectly and cannot run a chosen set of them on its own — the shape
    /// that tells a capability tester reading the wrapped validator's own answer apart from one
    /// reading only whether the interface is there.
    /// </summary>
    private sealed class CascadeStoppingOrderValidator : AbstractValidator<TestOrder>
    {
        public CascadeStoppingOrderValidator()
        {
            ClassLevelCascadeMode = CascadeMode.Stop;
            RuleFor(x => x.Description).NotEmpty();
        }
    }

    private static FluentValidationModelValidator<TestOrder> Adapter() =>
        new(new TestOrderValidator());

    // (a) The whole point, at the seam a caller actually tests: a subclass that overrides one
    // validation member and writes nothing else still presents both optional capabilities, and
    // both answer what the wrapped validator answers rather than merely being present. Mutation
    // that must break this: drop either optional interface from the base's declaration — the
    // members stay compiled and public, so only the interface assertions notice.
    [Fact]
    public async Task A_subclass_overriding_only_validation_keeps_both_optional_capabilities()
    {
        var inner = Adapter();
        IModelValidator<TestOrder> subject = new StagedAtOneEntryPoint(inner);

        var inspector = Assert.IsAssignableFrom<IRuleInspectingValidator<TestOrder>>(subject);
        Assert.True(inspector.CanInspectRules);
        Assert.Equal(
            FieldRequirement.Required,
            inspector.GetFieldRequirement(nameof(TestOrder.Description), ValidationProfile.Submit));
        Assert.Equal(
            inner.GetDeclaredFieldPaths(ValidationProfile.Submit).Order(StringComparer.Ordinal),
            inspector.GetDeclaredFieldPaths(ValidationProfile.Submit).Order(StringComparer.Ordinal));

        var ruleLevel = Assert.IsAssignableFrom<IRuleLevelValidator<TestOrder>>(subject);
        Assert.True(ruleLevel.CanValidateByRule);

        // Selection and execution are one capability, so reading the identities is only half of
        // it: one of them is run here, and the rule it names produces the failure a whole-profile
        // run produces for the same model.
        var rules = ruleLevel.SelectRules(ValidationProfile.Submit);
        Assert.Equal(inner.SelectRules(ValidationProfile.Submit).Count, rules.Count);

        var order = new TestOrder();
        var byRule = new List<ValidationIssue>();
        foreach (var rule in rules)
        {
            byRule.AddRange((await ruleLevel.ValidateRulesAsync(order, ValidationProfile.Submit, [rule])).Report.Issues);
        }

        Assert.Equal(
            (await inner.ValidateAsync(order, ValidationProfile.Submit)).Issues,
            byRule);
    }

    // (b) Over a validator with nothing beside the validation seam, each capability degrades the
    // way its OWN interface documents, and the two documented degrades differ: inspection reports
    // the empty answer, because an inspection answer decorates a form rather than deciding a
    // verdict, while rule-level execution throws, because running part of a profile and reporting
    // it as the whole would under-validate in silence. Mutation that must break this: forward
    // through a cast instead of a type test, and every line below raises InvalidCastException
    // where it expects an answer or a NotSupportedException.
    [Fact]
    public async Task Over_a_validator_without_the_capabilities_each_degrades_as_its_interface_documents()
    {
        IModelValidator<TestOrder> subject = new PassThroughValidator(new BareValidator());

        var inspector = Assert.IsAssignableFrom<IRuleInspectingValidator<TestOrder>>(subject);
        Assert.False(inspector.CanInspectRules);
        Assert.Equal(
            FieldRequirement.NotRequired,
            inspector.GetFieldRequirement(nameof(TestOrder.Description), ValidationProfile.Submit));
        Assert.Empty(inspector.GetDeclaredFieldPaths(ValidationProfile.Submit));

        var ruleLevel = Assert.IsAssignableFrom<IRuleLevelValidator<TestOrder>>(subject);
        Assert.False(ruleLevel.CanValidateByRule);
        Assert.Throws<NotSupportedException>(() => ruleLevel.SelectRules(ValidationProfile.Submit));
        Assert.Throws<NotSupportedException>(() => ruleLevel.GroupBySelectionClass([]));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => ruleLevel.ValidateRulesAsync(new TestOrder(), ValidationProfile.Submit, [default]));

        // And validation itself is untouched by any of that: the seam every validator has still
        // reaches the wrapped one.
        Assert.True((await subject.ValidateAsync(new TestOrder(), ValidationProfile.Submit)).IsValid);
    }

    // A tester answers the wrapped validator's own answer, not the presence of its interface. This
    // validator enumerates its rules perfectly and cannot run a chosen set of them on its own, so
    // the two gates part company — and the doer still throws, because forwarding hands the
    // question to the validator whose contract already promises that. Mutation that must break
    // this: test the interface alone (`_inner is IRuleLevelValidator<TModel>`), which reports a
    // capability the wrapped validator disclaims.
    [Fact]
    public void A_capability_tester_answers_the_wrapped_validators_answer_rather_than_its_type()
    {
        IModelValidator<TestOrder> subject = new PassThroughValidator(
            new FluentValidationModelValidator<TestOrder>(new CascadeStoppingOrderValidator()));

        Assert.False(Assert.IsAssignableFrom<IRuleLevelValidator<TestOrder>>(subject).CanValidateByRule);
        Assert.True(Assert.IsAssignableFrom<IRuleInspectingValidator<TestOrder>>(subject).CanInspectRules);
        Assert.Throws<NotSupportedException>(
            () => Assert.IsAssignableFrom<IRuleLevelValidator<TestOrder>>(subject).SelectRules(ValidationProfile.Submit));
    }

    // (c) The override is what runs, for a caller holding nothing but the validation seam. An
    // empty description fails the submit profile's presence rule; the wrapper stages one, so the
    // failure is gone — and the first assertion is what makes the second one about the override
    // rather than about the rules. Mutation that must break this: implement the seam explicitly
    // on the base and forward from there, which leaves the virtual member overridable and
    // uncalled.
    [Fact]
    public async Task The_overridden_validation_member_is_what_runs()
    {
        var order = new TestOrder();
        var inner = Adapter();

        Assert.Contains(
            (await inner.ValidateAsync(order, ValidationProfile.Submit)).Errors,
            issue => issue.Path == nameof(TestOrder.Description));

        IModelValidator<TestOrder> subject = new StagedAtOneEntryPoint(inner);
        Assert.DoesNotContain(
            (await subject.ValidateAsync(order, ValidationProfile.Submit)).Errors,
            issue => issue.Path == nameof(TestOrder.Description));

        // The model the caller handed over is not what was changed.
        Assert.Equal(string.Empty, order.Description);
    }

    // The obligation the class documents, pinned in the direction it warns about: which entry
    // point runs is the caller's choice, so a wrapper that changes what is validated at one of
    // them answers two ways. A caller taking the rule-level path sees the description still
    // missing; the same wrapper written at every entry point answers the same both ways.
    // Mutation that must break this: drop the ValidateRulesAsync override from
    // StagedAtEveryEntryPoint, and the second half stops agreeing.
    [Fact]
    public async Task Changing_what_is_validated_at_one_entry_point_leaves_the_rule_level_path_unchanged()
    {
        var order = new TestOrder();
        var inner = Adapter();

        Assert.Contains(
            await SubmitErrorsByRuleAsync(new StagedAtOneEntryPoint(inner), order),
            issue => issue.Path == nameof(TestOrder.Description));

        var everywhere = new StagedAtEveryEntryPoint(inner);
        Assert.DoesNotContain(
            await SubmitErrorsByRuleAsync(everywhere, order),
            issue => issue.Path == nameof(TestOrder.Description));
        Assert.DoesNotContain(
            (await everywhere.ValidateAsync(order, ValidationProfile.Submit)).Errors,
            issue => issue.Path == nameof(TestOrder.Description));
    }

    // Wrapping nothing is not a supported way to reach the base, so the argument is checked where
    // the mistake is made rather than at the first forwarded call.
    [Fact]
    public void Wrapping_nothing_is_rejected_at_construction() =>
        Assert.Throws<ArgumentNullException>(() => new PassThroughValidator(null!));

    /// <summary>
    /// Every submit-profile error <paramref name="validator"/> produces through the rule-level
    /// seam — one call per rule, the shape that attributes every error to the rule that produced
    /// it — asked for the way a caller finding the capability present asks, so a validator that
    /// has stopped presenting the capability fails the assertion rather than the compile.
    /// </summary>
    private static async Task<List<ValidationIssue>> SubmitErrorsByRuleAsync(
        IModelValidator<TestOrder> validator, TestOrder order)
    {
        var ruleLevel = Assert.IsAssignableFrom<IRuleLevelValidator<TestOrder>>(validator);
        var errors = new List<ValidationIssue>();
        foreach (var rule in ruleLevel.SelectRules(ValidationProfile.Submit))
        {
            errors.AddRange((await ruleLevel.ValidateRulesAsync(order, ValidationProfile.Submit, [rule])).Report.Errors);
        }

        return errors;
    }

    /// <summary>
    /// Implements both optional interfaces and disclaims both capabilities — the shape a type
    /// test and an answer-forwarding tester disagree about, in the direction that matters:
    /// present but disclaimed. A wrapped validator whose own testers change answer over its
    /// lifetime has exactly this shape at the moment it says no.
    /// </summary>
    private sealed class DisclaimingValidator
        : IModelValidator<TestOrder>, IRuleInspectingValidator<TestOrder>, IRuleLevelValidator<TestOrder>
    {
        public bool AskedForARequirement { get; private set; }

        public bool AskedForDeclaredPaths { get; private set; }

        public bool CanInspectRules => false;

        public bool CanValidateByRule => false;

        public Task<ValidationReport> ValidateAsync(
            TestOrder model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(Validate(model, profile));

        public ValidationReport Validate(TestOrder model, ValidationProfile profile) => new([]);

        public FieldRequirement GetFieldRequirement(string fieldPath, ValidationProfile profile)
        {
            AskedForARequirement = true;
            return FieldRequirement.NotRequired;
        }

        public IReadOnlySet<string> GetDeclaredFieldPaths(ValidationProfile profile)
        {
            AskedForDeclaredPaths = true;
            return System.Collections.Frozen.FrozenSet<string>.Empty;
        }

        public IReadOnlyList<RuleIdentity> SelectRules(ValidationProfile profile) =>
            throw new NotSupportedException();

        public Task<RuleLevelResult> ValidateRulesAsync(
            TestOrder model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<IReadOnlyList<RuleIdentity>> GroupBySelectionClass(
            IReadOnlyList<RuleIdentity> rules) =>
            throw new NotSupportedException();
    }

    // BOTH testers answer the wrapped validator's own answer rather than the presence of its
    // interface. The sibling above reaches the rule-level half of this through a cascade-stopping
    // FluentValidation validator, where the two gates part company for a reason of FV's own; this
    // one asks the question directly, and reaches the INSPECTION half, which no other test does
    // -- every other inner here either implements neither interface or answers true to both, so a
    // type test and a forwarded answer agree and nothing discriminates. Mutation that must break
    // this: either tester reduced to `_inner is I...Validator<TModel>`, which reports a capability
    // the wrapped validator disclaims.
    [Fact]
    public void Both_capability_testers_answer_a_wrapped_validator_that_disclaims_them()
    {
        IModelValidator<TestOrder> subject = new PassThroughValidator(new DisclaimingValidator());

        Assert.False(Assert.IsAssignableFrom<IRuleInspectingValidator<TestOrder>>(subject).CanInspectRules);
        Assert.False(Assert.IsAssignableFrom<IRuleLevelValidator<TestOrder>>(subject).CanValidateByRule);
    }

    // The readers forward on the interface being there, not on the tester's answer, so a wrapped
    // validator that disclaims inspection is still the one that answers -- which is what lets a
    // validator whose tester changes answer over its lifetime be followed rather than frozen.
    // Mutation that must break this: gate either reader on CanInspectRules and return the degrade,
    // which produces the same values from the wrapper while never asking the wrapped validator.
    [Fact]
    public void An_inspection_reader_asks_a_wrapped_validator_that_disclaims_the_capability()
    {
        var inner = new DisclaimingValidator();
        var inspector = Assert.IsAssignableFrom<IRuleInspectingValidator<TestOrder>>(
            new PassThroughValidator(inner));

        Assert.Equal(
            FieldRequirement.NotRequired,
            inspector.GetFieldRequirement(nameof(TestOrder.Description), ValidationProfile.Submit));
        Assert.Empty(inspector.GetDeclaredFieldPaths(ValidationProfile.Submit));

        Assert.True(inner.AskedForARequirement);
        Assert.True(inner.AskedForDeclaredPaths);
    }
}
