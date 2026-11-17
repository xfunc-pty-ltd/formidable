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

    // (a) The whole point, at the seam a caller actually tests: a subclass that overrides every
    // validation member still presents both optional capabilities, and both answer what the
    // wrapped validator answers rather than merely being present. Mutation that must break this:
    // drop either optional interface from the base's declaration — the members stay compiled and
    // public, so only the interface assertions notice.
    [Fact]
    public async Task A_subclass_overriding_every_validation_member_keeps_both_optional_capabilities()
    {
        var inner = Adapter();
        IModelValidator<TestOrder> subject = new StagedAtEveryEntryPoint(inner);

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
        // it: each of them is run here, and together they produce what the wrapper's own
        // ValidateAsync produces for the same model, the staged description included.
        var rules = ruleLevel.SelectRules(ValidationProfile.Submit);
        Assert.Equal(inner.SelectRules(ValidationProfile.Submit).Count, rules.Count);

        var order = new TestOrder();
        var byRule = new List<ValidationIssue>();
        foreach (var rule in rules)
        {
            byRule.AddRange((await ruleLevel.ValidateRulesAsync(order, ValidationProfile.Submit, [rule])).Report.Issues);
        }

        Assert.Equal(
            (await subject.ValidateAsync(order, ValidationProfile.Submit)).Issues,
            byRule);
    }

    // A subclass that overrides ValidateAsync and leaves ValidateRulesAsync as the base wrote it
    // keeps inspection and the rule-level interface, answers that it cannot validate by rule, and
    // refuses the three members that select or run rules as that answer promises. Mutation that
    // must break this: let the base's capability tester forward the wrapped validator's answer
    // alone, and the subclass claims a capability that never reaches its override.
    [Fact]
    public async Task A_subclass_overriding_ValidateAsync_alone_keeps_inspection_and_is_validated_whole()
    {
        IModelValidator<TestOrder> subject = new StagedAtOneEntryPoint(Adapter());

        var inspector = Assert.IsAssignableFrom<IRuleInspectingValidator<TestOrder>>(subject);
        Assert.True(inspector.CanInspectRules);
        Assert.Equal(
            FieldRequirement.Required,
            inspector.GetFieldRequirement(nameof(TestOrder.Description), ValidationProfile.Submit));

        var ruleLevel = Assert.IsAssignableFrom<IRuleLevelValidator<TestOrder>>(subject);
        Assert.False(ruleLevel.CanValidateByRule);
        var refusal = Assert.Throws<NotSupportedException>(() => ruleLevel.SelectRules(ValidationProfile.Submit));
        Assert.Contains(nameof(StagedAtOneEntryPoint), refusal.Message);
        Assert.Throws<NotSupportedException>(() => ruleLevel.GroupBySelectionClass([]));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => ruleLevel.ValidateRulesAsync(new TestOrder(), ValidationProfile.Submit, [default]));

        Assert.DoesNotContain(
            (await subject.ValidateAsync(new TestOrder(), ValidationProfile.Submit)).Errors,
            issue => issue.Path == nameof(TestOrder.Description));
    }

    /// <summary>The validation members a <see cref="Shaped"/> subclass overrides, and where.</summary>
    public enum OverrideShape
    {
        None,
        ValidateAsyncAlone,
        ValidateAlone,
        ValidateRulesAsyncAlone,
        ValidateAsyncAndValidateRulesAsync,
        AllThree,
        ValidateAsyncInheritedFromABase,
        ValidateAsyncBelowABaseOverridingBoth,
        ValidateRulesAsyncBelowABaseOverridingValidateAsync,
    }

    private class OverridesValidateAsync(IModelValidator<TestOrder> inner) : DelegatingModelValidator<TestOrder>(inner)
    {
        public override Task<ValidationReport> ValidateAsync(
            TestOrder model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
            base.ValidateAsync(model, profile, cancellationToken);
    }

    private sealed class OverridesValidate(IModelValidator<TestOrder> inner) : DelegatingModelValidator<TestOrder>(inner)
    {
        public override ValidationReport Validate(TestOrder model, ValidationProfile profile) => base.Validate(model, profile);
    }

    private sealed class OverridesValidateRulesAsync(IModelValidator<TestOrder> inner) : DelegatingModelValidator<TestOrder>(inner)
    {
        public override Task<RuleLevelResult> ValidateRulesAsync(
            TestOrder model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules,
            CancellationToken cancellationToken = default) =>
            base.ValidateRulesAsync(model, profile, rules, cancellationToken);
    }

    private class OverridesBoth(IModelValidator<TestOrder> inner) : DelegatingModelValidator<TestOrder>(inner)
    {
        public override Task<ValidationReport> ValidateAsync(
            TestOrder model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
            base.ValidateAsync(model, profile, cancellationToken);

        public override Task<RuleLevelResult> ValidateRulesAsync(
            TestOrder model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules,
            CancellationToken cancellationToken = default) =>
            base.ValidateRulesAsync(model, profile, rules, cancellationToken);
    }

    private sealed class OverridesAllThree(IModelValidator<TestOrder> inner) : OverridesBoth(inner)
    {
        public override ValidationReport Validate(TestOrder model, ValidationProfile profile) => base.Validate(model, profile);
    }

    private sealed class InheritsValidateAsync(IModelValidator<TestOrder> inner) : OverridesValidateAsync(inner);

    private sealed class ValidateAsyncBelowBoth(IModelValidator<TestOrder> inner) : OverridesBoth(inner)
    {
        public override Task<ValidationReport> ValidateAsync(
            TestOrder model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
            base.ValidateAsync(model, profile, cancellationToken);
    }

    private sealed class ValidateRulesAsyncBelowValidateAsync(IModelValidator<TestOrder> inner) : OverridesValidateAsync(inner)
    {
        public override Task<RuleLevelResult> ValidateRulesAsync(
            TestOrder model, ValidationProfile profile, IReadOnlyList<RuleIdentity> rules,
            CancellationToken cancellationToken = default) =>
            base.ValidateRulesAsync(model, profile, rules, cancellationToken);
    }

    private static DelegatingModelValidator<TestOrder> Shaped(OverrideShape shape, IModelValidator<TestOrder> inner) => shape switch
    {
        OverrideShape.None => new PassThroughValidator(inner),
        OverrideShape.ValidateAsyncAlone => new OverridesValidateAsync(inner),
        OverrideShape.ValidateAlone => new OverridesValidate(inner),
        OverrideShape.ValidateRulesAsyncAlone => new OverridesValidateRulesAsync(inner),
        OverrideShape.ValidateAsyncAndValidateRulesAsync => new OverridesBoth(inner),
        OverrideShape.AllThree => new OverridesAllThree(inner),
        OverrideShape.ValidateAsyncInheritedFromABase => new InheritsValidateAsync(inner),
        OverrideShape.ValidateAsyncBelowABaseOverridingBoth => new ValidateAsyncBelowBoth(inner),
        _ => new ValidateRulesAsyncBelowValidateAsync(inner),
    };

    // A form takes a validator rule by rule through ValidateRulesAsync alone, so a ValidateAsync
    // override with no ValidateRulesAsync override in its own class or one below it would never
    // run there: the capability goes. An override of the synchronous Validate is never reached by
    // a form on either path, so it leaves the capability on. Over a validator that cannot be taken
    // rule by rule, every shape answers false. Mutations that must break this: answer "no
    // override" from the detection (the three false rows read true), or withdraw on any
    // ValidateAsync override (the rows with a ValidateRulesAsync override in the same class or
    // below read false).
    [Theory]
    [InlineData(OverrideShape.None, true)]
    [InlineData(OverrideShape.ValidateAsyncAlone, false)]
    [InlineData(OverrideShape.ValidateAlone, true)]
    [InlineData(OverrideShape.ValidateRulesAsyncAlone, true)]
    [InlineData(OverrideShape.ValidateAsyncAndValidateRulesAsync, true)]
    [InlineData(OverrideShape.AllThree, true)]
    [InlineData(OverrideShape.ValidateAsyncInheritedFromABase, false)]
    [InlineData(OverrideShape.ValidateAsyncBelowABaseOverridingBoth, false)]
    [InlineData(OverrideShape.ValidateRulesAsyncBelowABaseOverridingValidateAsync, true)]
    public void A_subclass_keeps_the_capability_unless_a_form_would_miss_its_ValidateAsync(OverrideShape shape, bool keeps)
    {
        IRuleLevelValidator<TestOrder> subject = Shaped(shape, Adapter());
        IRuleLevelValidator<TestOrder> overIncapable = Shaped(
            shape, new FluentValidationModelValidator<TestOrder>(new CascadeStoppingOrderValidator()));

        Assert.Equal(keeps, subject.CanValidateByRule);
        Assert.False(overIncapable.CanValidateByRule);
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

    // The obligation the class documents, from the side a form sees: a caller asks the capability
    // and takes the rule-level path only where it holds, so a wrapper staging its model at
    // ValidateAsync alone and one staging it at every entry point both have their staged
    // description judged. Mutation that must break this: answer "no override" from the base's
    // detection, and the wrapper written at one entry point is taken rule by rule past its stage.
    [Fact]
    public async Task A_form_judges_the_staged_model_however_many_entry_points_the_wrapper_overrides()
    {
        var order = new TestOrder();
        var inner = Adapter();

        Assert.DoesNotContain(
            await SubmitErrorsAsAFormAsksAsync(new StagedAtOneEntryPoint(inner), order),
            issue => issue.Path == nameof(TestOrder.Description));
        Assert.DoesNotContain(
            await SubmitErrorsAsAFormAsksAsync(new StagedAtEveryEntryPoint(inner), order),
            issue => issue.Path == nameof(TestOrder.Description));

        // The control: the rules themselves still fail the unstaged description.
        Assert.Contains(
            await SubmitErrorsAsAFormAsksAsync(inner, order),
            issue => issue.Path == nameof(TestOrder.Description));
    }

    // Wrapping nothing is not a supported way to reach the base, so the argument is checked where
    // the mistake is made rather than at the first forwarded call.
    [Fact]
    public void Wrapping_nothing_is_rejected_at_construction() =>
        Assert.Throws<ArgumentNullException>(() => new PassThroughValidator(null!));

    /// <summary>
    /// Every submit-profile error <paramref name="validator"/> produces as a form asks for it:
    /// through the rule-level seam, one call per rule, where the capability holds, and through
    /// <see cref="IModelValidator{TModel}.ValidateAsync"/> otherwise.
    /// </summary>
    private static async Task<List<ValidationIssue>> SubmitErrorsAsAFormAsksAsync(
        IModelValidator<TestOrder> validator, TestOrder order)
    {
        if (validator is not IRuleLevelValidator<TestOrder> { CanValidateByRule: true } ruleLevel)
        {
            return [.. (await validator.ValidateAsync(order, ValidationProfile.Submit)).Errors];
        }

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
