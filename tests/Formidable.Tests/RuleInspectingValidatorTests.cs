using FluentValidation;
using FluentValidation.Results;
using Formidable.Sample.Shared;

namespace Formidable.Tests;

/// <summary>
/// Pins <see cref="IRuleInspectingValidator{TModel}"/> on the FluentValidation adapter: the
/// answer is scoped by the profile's rule selection, a conditional presence rule is told apart
/// from an unconditional one however the condition was written, inspection survives a
/// class-level cascade stop that bars per-rule execution, a validator that cannot be read says
/// so instead of throwing, and the per-field code map partitions presence from everything else
/// using the codes FluentValidation actually puts on the failures.
/// </summary>
public class RuleInspectingValidatorTests
{
    private sealed class InspectModel
    {
        public string Title { get; set; } = string.Empty;
        public string Chained { get; set; } = string.Empty;
        public string? Wrapped { get; set; }
        public string CurrentOnly { get; set; } = string.Empty;
        public string Deferred { get; set; } = string.Empty;
        public string Withheld { get; set; } = string.Empty;
        public string Mixed { get; set; } = string.Empty;
        public string Both { get; set; } = string.Empty;
        public string Predicate { get; set; } = string.Empty;
        public string? Absent { get; set; }
        public bool Gate { get; set; }
        public InspectChild Child { get; set; } = new();
        public List<string> Tags { get; set; } = [];
    }

    private sealed class InspectChild
    {
        public string City { get; set; } = string.Empty;
    }

    private sealed class InspectChildValidator : AbstractValidator<InspectChild>
    {
        public InspectChildValidator() => RuleFor(c => c.City).NotEmpty();
    }

    /// <summary>A presence rule declared per element of a collection.</summary>
    private sealed class CollectionRuleValidator : AbstractValidator<InspectModel>
    {
        public CollectionRuleValidator() => RuleForEach(m => m.Tags).NotEmpty();
    }

    /// <summary>
    /// One rule of every condition shape FluentValidation can express, two rules whose answer
    /// turns on where the condition landed rather than on whether one exists, and two shapes
    /// that express presence in a way no inspection can see. Every rule rides the default
    /// bucket, so a profile including default rules selects them all and the profile axis stays
    /// out of this fixture's way.
    /// </summary>
    private sealed class ShapeValidator : AbstractValidator<InspectModel>
    {
        public ShapeValidator()
        {
            RuleFor(m => m.Title).NotEmpty();
            RuleFor(m => m.Chained).NotEmpty().When(m => m.Gate);
            When(m => m.Gate, () => RuleFor(m => m.Wrapped).NotNull());
            RuleFor(m => m.CurrentOnly).NotEmpty().When(m => m.Gate, ApplyConditionTo.CurrentValidator);
            RuleFor(m => m.Deferred).NotEmpty().WhenAsync((m, _) => Task.FromResult(m.Gate));
            RuleFor(m => m.Withheld).NotEmpty().Unless(m => m.Gate);
            // The condition attaches to the length check alone, leaving the presence check
            // ahead of it unconditional.
            RuleFor(m => m.Mixed).NotEmpty().MaximumLength(5).When(m => m.Gate, ApplyConditionTo.CurrentValidator);
            RuleFor(m => m.Both).NotEmpty().When(m => m.Gate);
            RuleFor(m => m.Both).NotEmpty();
            RuleFor(m => m.Predicate).Must(value => !string.IsNullOrWhiteSpace(value));
            RuleFor(m => m.Absent).Null();
        }
    }

    /// <summary>An AbstractValidator whose class-level cascade stops on the first failing rule.</summary>
    private sealed class CascadeStopValidator : AbstractValidator<InspectModel>
    {
        public CascadeStopValidator()
        {
            ClassLevelCascadeMode = CascadeMode.Stop;
            RuleFor(m => m.Title).NotEmpty();
        }
    }

    /// <summary>
    /// One field carrying a presence and a length component under custom codes, one carrying
    /// the same pair under FluentValidation's default codes, one whose two components share a
    /// single custom code, and one holding a child validator whose failures belong to the
    /// child's own paths.
    /// </summary>
    private sealed class CodeMapValidator : AbstractValidator<InspectModel>
    {
        public CodeMapValidator()
        {
            RuleFor(m => m.Title).NotEmpty().WithErrorCode("TITLE_MISSING");
            RuleFor(m => m.Title).MaximumLength(5).WithErrorCode("TITLE_LONG");
            RuleFor(m => m.Chained).NotEmpty();
            RuleFor(m => m.Chained).MaximumLength(5);
            RuleFor(m => m.Both).NotEmpty().WithErrorCode("BOTH");
            RuleFor(m => m.Both).MaximumLength(5).WithErrorCode("BOTH");
            RuleFor(m => m.Child).SetValidator(new InspectChildValidator());
        }
    }

    /// <summary>An IValidator implemented by hand — no rule enumeration surface at all.</summary>
    private sealed class HandRolledValidator : IValidator<InspectModel>
    {
        public ValidationResult Validate(InspectModel instance) => new();

        public Task<ValidationResult> ValidateAsync(InspectModel instance, CancellationToken cancellation = default) =>
            Task.FromResult(new ValidationResult());

        public ValidationResult Validate(IValidationContext context) => new();

        public Task<ValidationResult> ValidateAsync(IValidationContext context, CancellationToken cancellation = default) =>
            Task.FromResult(new ValidationResult());

        public IValidatorDescriptor CreateDescriptor() => throw new NotSupportedException();

        public bool CanValidateInstancesOfType(Type type) => type == typeof(InspectModel);
    }

    /// <summary>
    /// The profile's rule selection decides the answer. DraftedBrief puts Title's length rule in
    /// the draft bucket and its presence rule in the submit bucket, so the same field answers
    /// differently under the two profiles — and the draft answer is a real reading of a selected
    /// rule, not silence, which the code map alongside it shows.
    /// </summary>
    [Fact]
    public void The_profile_decides_whether_a_field_is_required()
    {
        var adapter = new FluentValidationModelValidator<DraftedBrief>(new DraftedBriefValidator());

        Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Title", ValidationProfile.Submit));
        Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Summary", ValidationProfile.Submit));
        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Title", ValidationProfile.Draft));
        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Summary", ValidationProfile.Draft));

        var draftCodes = adapter.GetFieldRuleCodes(ValidationProfile.Draft);
        Assert.Empty(draftCodes["Title"].PresenceCodes);
        Assert.Contains("MaximumLengthValidator", draftCodes["Title"].OtherCodes);
        // Summary's only rule is the submit-bucket presence rule, so the draft profile reaches
        // no component of it at all.
        Assert.False(draftCodes.ContainsKey("Summary"));
    }

    /// <summary>
    /// A presence rule reached only through a condition answers conditionally required, for
    /// every place FluentValidation can record the condition — a When chained after the
    /// component, a When block wrapping the rule declaration, the CurrentValidator form, the
    /// asynchronous form, and Unless, which FluentValidation records as a negated When. Reading
    /// the rule-level flag alone reports four of the five as unconditional.
    /// </summary>
    [Theory]
    [InlineData("Chained")]
    [InlineData("Wrapped")]
    [InlineData("CurrentOnly")]
    [InlineData("Deferred")]
    [InlineData("Withheld")]
    public void A_conditional_presence_rule_is_conditionally_required(string field)
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new ShapeValidator());

        Assert.Equal(RuleRequirement.ConditionallyRequired, adapter.GetFieldRequirement(field, ValidationProfile.Draft));
    }

    /// <summary>
    /// The condition has to be read on the presence component itself: the CurrentValidator form
    /// marks one component and leaves its siblings alone, so a rule whose length check is
    /// conditional still demands a value unconditionally. Reading "any component of this rule
    /// carries a condition" would call this one conditional.
    /// </summary>
    [Fact]
    public void A_condition_on_a_sibling_component_leaves_presence_unconditional()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new ShapeValidator());

        Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Mixed", ValidationProfile.Draft));
    }

    /// <summary>An unconditional presence rule outranks a conditional one on the same field.</summary>
    [Fact]
    public void An_unconditional_presence_rule_wins_over_a_conditional_one()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new ShapeValidator());

        Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Both", ValidationProfile.Draft));
    }

    /// <summary>
    /// The honest limit this pins rather than fixes: presence written as a predicate is
    /// indistinguishable from any other predicate and reads as not required — which is why a
    /// consumer-facing feature built on this must let a consumer say so directly. Alongside it,
    /// the opposite rule stays out: Null() demands emptiness, and its marker interface is spelled
    /// three characters away from the one presence detection matches.
    /// </summary>
    [Fact]
    public void Presence_expressed_as_a_predicate_is_invisible_and_a_null_rule_is_not_presence()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new ShapeValidator());

        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Predicate", ValidationProfile.Draft));
        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Absent", ValidationProfile.Draft));
    }

    /// <summary>A field no rule mentions is not required, and asking about it is not an error.</summary>
    [Fact]
    public void An_unruled_field_is_not_required()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new ShapeValidator());

        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Nothing", ValidationProfile.Draft));
        Assert.False(adapter.GetFieldRuleCodes(ValidationProfile.Draft).ContainsKey("Nothing"));
    }

    /// <summary>
    /// Inspection has its own gate. A class-level cascade stop bars per-rule execution because
    /// separate executions cannot reproduce it, but it does not stop the validator enumerating
    /// what it declares — so the field still reports required. Reusing the execution gate would
    /// refuse this validator for a reason inspection never runs into.
    /// </summary>
    [Fact]
    public void A_cascade_stop_validator_is_inspectable_though_it_cannot_be_executed_by_rule()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new CascadeStopValidator());

        Assert.False(adapter.CanValidateByRule);
        Assert.True(adapter.CanInspectRules);
        Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Title", ValidationProfile.Draft));
        Assert.Contains("NotEmptyValidator", adapter.GetFieldRuleCodes(ValidationProfile.Draft)["Title"].PresenceCodes);
    }

    /// <summary>
    /// A validator that keeps its rules to itself reports that it cannot be read and answers
    /// emptily — it never throws, because an inspection answer decorates a form rather than
    /// deciding a verdict. This fixture's descriptor throws, so any blind call to it fails here.
    /// </summary>
    [Fact]
    public void A_hand_rolled_validator_reports_that_it_cannot_be_inspected()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new HandRolledValidator());

        Assert.False(adapter.CanInspectRules);
        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Title", ValidationProfile.Draft));
        Assert.Empty(adapter.GetFieldRuleCodes(ValidationProfile.Draft));
    }

    /// <summary>
    /// The code map partitions one field's codes by what the component producing them checks,
    /// and it keys on the configured code where there is one — so a presence rule wearing a
    /// custom code is still recognised as presence. Matching the default code text instead would
    /// misread that field's failure as "the value is wrong" on a field that is merely empty.
    /// </summary>
    [Fact]
    public void The_code_map_separates_presence_from_everything_else()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new CodeMapValidator());

        var codes = adapter.GetFieldRuleCodes(ValidationProfile.Draft);

        Assert.Equal(["TITLE_MISSING"], codes["Title"].PresenceCodes);
        Assert.Equal(["TITLE_LONG"], codes["Title"].OtherCodes);
        Assert.Empty(codes["Title"].AmbiguousCodes);

        Assert.Equal(["NotEmptyValidator"], codes["Chained"].PresenceCodes);
        Assert.Equal(["MaximumLengthValidator"], codes["Chained"].OtherCodes);
    }

    /// <summary>
    /// A rule declared per element of a collection is read under the collection's own path: its
    /// presence component makes that path required, while the failure it produces carries an
    /// indexed path the map has no key for. A caller matching failures to codes finds nothing
    /// for the indexed path and treats it as it treats any unmapped field.
    /// </summary>
    [Fact]
    public void A_collection_rule_answers_under_the_collection_path_not_the_indexed_one()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new CollectionRuleValidator());

        Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Tags", ValidationProfile.Draft));
        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Tags[0]", ValidationProfile.Draft));

        var codes = adapter.GetFieldRuleCodes(ValidationProfile.Draft);
        Assert.Equal(["NotEmptyValidator"], codes["Tags"].PresenceCodes);
        Assert.False(codes.ContainsKey("Tags[0]"));
    }

    /// <summary>
    /// A child validator's component stays out of the map: its failures carry the child's own
    /// paths, so filing its code under the field that holds the child would attribute the
    /// child's complaint to its parent.
    /// </summary>
    [Fact]
    public void A_child_validator_component_contributes_no_code_to_the_field_carrying_it()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new CodeMapValidator());

        Assert.False(adapter.GetFieldRuleCodes(ValidationProfile.Draft).ContainsKey("Child"));
    }

    /// <summary>
    /// A code both kinds of component can produce identifies neither, so it is reported as
    /// ambiguous and withheld from both sets — the caller sees the ambiguity instead of a guess,
    /// and a caller testing the presence set alone reads the failure as a wrong value, which
    /// shows it rather than hiding it.
    /// </summary>
    [Fact]
    public void A_code_shared_by_both_kinds_of_component_is_reported_as_ambiguous()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new CodeMapValidator());

        var both = adapter.GetFieldRuleCodes(ValidationProfile.Draft)["Both"];

        Assert.Equal(["BOTH"], both.AmbiguousCodes);
        Assert.Empty(both.PresenceCodes);
        Assert.Empty(both.OtherCodes);
    }

    /// <summary>
    /// The map's codes are the ones that turn up on real failures: validating an empty model
    /// under the profile the map was built for produces issues whose codes the map already
    /// carries in the presence set for their own field. This is what lets a caller holding only
    /// a report decide that a field is empty rather than wrong.
    /// </summary>
    [Fact]
    public async Task The_mapped_codes_are_the_codes_the_failures_carry()
    {
        var adapter = new FluentValidationModelValidator<DraftedBrief>(new DraftedBriefValidator());
        var codes = adapter.GetFieldRuleCodes(ValidationProfile.Submit);

        var report = await adapter.ValidateAsync(new DraftedBrief(), ValidationProfile.Submit);

        Assert.Equal(2, report.Issues.Count);
        foreach (var issue in report.Issues)
        {
            var code = issue.Code;
            Assert.NotNull(code);
            Assert.Contains(code, codes[issue.Path].PresenceCodes);
        }
    }

    /// <summary>
    /// Inspection runs the wrapped validator's own ruleset-name verification, so a profile
    /// naming a ruleset that was never registered fails here exactly as validating with it
    /// would. Answering quietly instead would report every submit-bucket field as not required —
    /// the silent under-report the ruleset check exists to prevent.
    /// </summary>
    [Fact]
    public void A_profile_naming_an_unregistered_ruleset_throws()
    {
        var adapter = new FluentValidationModelValidator<DraftedBrief>(new DraftedBriefValidator());
        var profile = ValidationProfile.Named("Typo", includeDefaultRules: true, "Sumbit");

        Assert.Throws<InvalidOperationException>(() => adapter.GetFieldRequirement("Title", profile));
        Assert.Throws<InvalidOperationException>(() => adapter.GetFieldRuleCodes(profile));
    }
}
