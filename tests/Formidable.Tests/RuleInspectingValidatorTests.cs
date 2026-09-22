using System.Reflection;
using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Results;
using FluentValidation.Validators;
using Formidable.Sample.Shared;

namespace Formidable.Tests;

/// <summary>
/// Pins <see cref="IRuleInspectingValidator{TModel}"/> on the FluentValidation adapter: the
/// answer is scoped by the profile's rule selection, a conditional presence rule is told apart
/// from an unconditional one however the condition was written, inspection survives a
/// class-level cascade stop that bars per-rule execution, a validator that cannot be read says
/// so instead of throwing, and the declared-path enumeration reports the validator's own shape
/// — child validators and Include()d rules included, collection indexes left open.
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
        public List<InspectChild> Rows { get; set; } = [];
    }

    private sealed class InspectChild
    {
        public string City { get; set; } = string.Empty;
        public string Zip { get; set; } = string.Empty;
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

    /// <summary>A validator whose rules arrive through Include() rather than its own declarations.</summary>
    private sealed class IncludedPartValidator : AbstractValidator<InspectModel>
    {
        public IncludedPartValidator() => RuleFor(m => m.Chained).NotEmpty();
    }

    /// <summary>One rule of its own and one merged in — the shape Include() produces.</summary>
    private sealed class IncludingValidator : AbstractValidator<InspectModel>
    {
        public IncludingValidator()
        {
            RuleFor(m => m.Title).NotEmpty();
            Include(new IncludedPartValidator());
        }
    }

    /// <summary>A child validator reached only through a condition on the component holding it.</summary>
    private sealed class ConditionalChildValidator : AbstractValidator<InspectModel>
    {
        public ConditionalChildValidator() =>
            RuleFor(m => m.Child).SetValidator(new InspectChildValidator()).When(m => m.Gate);
    }

    /// <summary>A collection whose child validator is reached only through a condition on the
    /// carrying rule.</summary>
    private sealed class RowConditionalValidator : AbstractValidator<InspectModel>
    {
        public RowConditionalValidator() =>
            RuleForEach(m => m.Rows).SetValidator(new InspectChildValidator()).When(m => m.Gate);
    }

    /// <summary>A collection whose rows are filtered before validation — the per-row
    /// <c>Where</c>, which FluentValidation stores as the collection rule's <c>Filter</c>
    /// rather than as a condition flag.</summary>
    private sealed class RowFilteredValidator : AbstractValidator<InspectModel>
    {
        public RowFilteredValidator() =>
            RuleForEach(m => m.Rows).Where(r => r.Zip.Length > 0).SetValidator(new InspectChildValidator());
    }

    /// <summary>The same filter in its asynchronous form, stored as <c>AsyncFilter</c>.</summary>
    private sealed class RowAsyncFilteredValidator : AbstractValidator<InspectModel>
    {
        public RowAsyncFilteredValidator() =>
            RuleForEach(m => m.Rows)
                .WhereAsync(r => Task.FromResult(r.Zip.Length > 0))
                .SetValidator(new InspectChildValidator());
    }

    /// <summary>
    /// A child validator produced by a lambda that reads the parent model, beside an ordinary
    /// rule — so an answer covering only the ordinary one shows the lambda went unread rather
    /// than the walk having stopped.
    /// </summary>
    private sealed class LambdaChildValidator : AbstractValidator<InspectModel>
    {
        public LambdaChildValidator()
        {
            RuleFor(m => m.Title).NotEmpty();
            RuleFor(m => m.Child).SetValidator((model, _) => model.Gate
                ? new InspectChildValidator()
                : new InspectChildValidator());
        }
    }

    /// <summary>A model whose shape recurses through itself.</summary>
    private sealed class InspectNode
    {
        public string Label { get; set; } = string.Empty;

        public List<InspectNode> Children { get; set; } = [];
    }

    /// <summary>A validator that hands its own children to itself.</summary>
    private sealed class InspectNodeValidator : AbstractValidator<InspectNode>
    {
        public InspectNodeValidator()
        {
            RuleFor(n => n.Label).NotEmpty();
            RuleForEach(n => n.Children).SetValidator(this);
        }
    }

    /// <summary>Rewrites every index in a failure path to the open form the templates use.</summary>
    private static string Templated(string path) => Regex.Replace(path, @"\[[^\]]*\]", "[]");

    /// <summary>A model-level rule carrying a child validator for the model's own type.</summary>
    private sealed class ModelLevelChildValidator : AbstractValidator<InspectModel>
    {
        public ModelLevelChildValidator() => RuleFor(m => m).SetValidator(new IncludedPartValidator());
    }

    /// <summary>The same shape written as a <c>ChildRules</c> block.</summary>
    private sealed class ModelLevelChildRulesValidator : AbstractValidator<InspectModel>
    {
        public ModelLevelChildRulesValidator() =>
            RuleFor(m => m).ChildRules(model => model.RuleFor(x => x.Chained).NotEmpty());
    }

    /// <summary>
    /// A child validator scoped by the <c>SetValidator</c> call rather than by its own rules —
    /// the child runs under the call's rulesets, whatever the profile names.
    /// </summary>
    private sealed class RuleSetScopedChildValidator : AbstractValidator<InspectModel>
    {
        public RuleSetScopedChildValidator() =>
            RuleFor(m => m.Child).SetValidator(new InspectChildValidator(), "Admin");
    }

    /// <summary>The same demand scoped inside the child's own rules, where the selector in
    /// force at the child decides it.</summary>
    private sealed class TaggedChildValidator : AbstractValidator<InspectChild>
    {
        public TaggedChildValidator() => RuleSet("Admin", () => RuleFor(c => c.City).NotEmpty());
    }

    /// <summary>A root holding the self-scoping child above.</summary>
    private sealed class SelfScopingChildRootValidator : AbstractValidator<InspectModel>
    {
        public SelfScopingChildRootValidator() => RuleFor(m => m.Child).SetValidator(new TaggedChildValidator());
    }

    /// <summary>A root scoping the tagged child by the <c>SetValidator</c> call as well.</summary>
    private sealed class ScopedTaggedChildRootValidator : AbstractValidator<InspectModel>
    {
        public ScopedTaggedChildRootValidator() =>
            RuleFor(m => m.Child).SetValidator(new TaggedChildValidator(), "Admin");
    }

    /// <summary>A model one level deeper, so a scoped child can hold a scoped child.</summary>
    private sealed class NestedScopeModel
    {
        public InspectOuter Outer { get; set; } = new();
    }

    private sealed class InspectOuter
    {
        public InspectChild Inner { get; set; } = new();
    }

    /// <summary>One "Special"-tagged demand beside an untagged one, for the inner boundary.</summary>
    private sealed class SpecialTaggedChildValidator : AbstractValidator<InspectChild>
    {
        public SpecialTaggedChildValidator()
        {
            RuleSet("Special", () => RuleFor(c => c.City).NotEmpty());
            RuleFor(c => c.Zip).NotEmpty();
        }
    }

    /// <summary>An "Admin"-tagged holding rule scoping its own child into "Special".</summary>
    private sealed class InspectOuterValidator : AbstractValidator<InspectOuter>
    {
        public InspectOuterValidator() =>
            RuleSet("Admin", () => RuleFor(o => o.Inner).SetValidator(new SpecialTaggedChildValidator(), "Special"));
    }

    /// <summary>A scoped child holding a scoped child.</summary>
    private sealed class NestedScopedRootValidator : AbstractValidator<NestedScopeModel>
    {
        public NestedScopedRootValidator() =>
            RuleFor(m => m.Outer).SetValidator(new InspectOuterValidator(), "Admin");
    }

    /// <summary>
    /// A <c>ChildRules</c> block declared inside a ruleset — the shape whose selection lives on
    /// the child's own rules: FluentValidation tags them with the declaring scope's rulesets
    /// and writes no scope onto the adaptor.
    /// </summary>
    private sealed class TaggedChildRulesValidator : AbstractValidator<InspectModel>
    {
        public TaggedChildRulesValidator() =>
            RuleSet("Submit", () => RuleFor(m => m.Child).ChildRules(c => c.RuleFor(x => x.City).NotEmpty()));
    }

    /// <summary>One rule the stock selector would select under Draft, one only a replaced factory's would.</summary>
    private sealed class FactoryProbeValidator : AbstractValidator<InspectModel>
    {
        public FactoryProbeValidator()
        {
            RuleFor(m => m.Title).NotEmpty();
            RuleSet("FactoryProbe", () => RuleFor(m => m.Chained).NotEmpty());
        }
    }

    /// <summary>
    /// Admits any rule tagged "FactoryProbe" whatever the name list says, and answers like the
    /// stock ruleset selector for everything else — so installing it as the global factory
    /// flips exactly one fixture's answers and touches nothing that may be validating in
    /// parallel.
    /// </summary>
    private sealed class FactoryProbeSelector : IValidatorSelector
    {
        private readonly IValidatorSelector _stock;

        public FactoryProbeSelector(IEnumerable<string> ruleSets) => _stock = new RulesetValidatorSelector(ruleSets);

        public bool CanExecute(IValidationRule rule, string propertyPath, IValidationContext context) =>
            rule.RuleSets?.Contains("FactoryProbe", StringComparer.Ordinal) == true
            || _stock.CanExecute(rule, propertyPath, context);
    }

    /// <summary>A leaf pair for the equivalence harness: one untagged demand, one tagged "Admin".</summary>
    private sealed class EquivalenceChild
    {
        public string City { get; set; } = string.Empty;
        public string Zip { get; set; } = string.Empty;
    }

    private sealed class EquivalenceChildValidator : AbstractValidator<EquivalenceChild>
    {
        public EquivalenceChildValidator()
        {
            RuleFor(c => c.City).NotEmpty();
            RuleSet("Admin", () => RuleFor(c => c.Zip).NotEmpty());
        }
    }

    /// <summary>A branch whose only route in is an untagged rule holding an unscoped child.</summary>
    private sealed class EquivalenceNest
    {
        public EquivalenceChild Inner { get; set; } = new();
    }

    private sealed class EquivalenceNestValidator : AbstractValidator<EquivalenceNest>
    {
        public EquivalenceNestValidator() => RuleFor(n => n.Inner).SetValidator(new EquivalenceChildValidator());
    }

    /// <summary>A row judged only through inline child rules, holding a collection judged the same way.</summary>
    private sealed class EquivalenceTeam
    {
        public string Name { get; set; } = string.Empty;
        public List<EquivalenceChild> Members { get; set; } = [new()];
    }

    private sealed class EquivalenceModel
    {
        public string Plain { get; set; } = string.Empty;
        public string SubmitTagged { get; set; } = string.Empty;
        public string AdminTagged { get; set; } = string.Empty;
        public string CommaTagged { get; set; } = string.Empty;
        public string Included { get; set; } = string.Empty;
        public string EngagedIncluded { get; set; } = string.Empty;
        public EquivalenceChild Unscoped { get; set; } = new();
        public EquivalenceChild ScopedAdmin { get; set; } = new();
        public EquivalenceChild ScopedAdminDefault { get; set; } = new();
        public EquivalenceChild ScopedWildcard { get; set; } = new();
        public EquivalenceChild Bundled { get; set; } = new();
        public EquivalenceNest Nested { get; set; } = new();
        public List<EquivalenceChild> Sites { get; set; } = [new()];
        public List<EquivalenceTeam> Teams { get; set; } = [new()];
    }

    private sealed class IncludedEquivalencePart : AbstractValidator<EquivalenceModel>
    {
        public IncludedEquivalencePart() => RuleFor(m => m.Included).NotEmpty();
    }

    private sealed class EngagedEquivalencePart : AbstractValidator<EquivalenceModel>
    {
        public EngagedEquivalencePart() => RuleFor(m => m.EngagedIncluded).NotEmpty();
    }

    /// <summary>
    /// Every composition shape the shipped samples use, every rule a presence rule — so a run
    /// over an all-empty model fails exactly what it selects, and "what executed" is readable
    /// off the report. The Teams branch is the samples' own child-composition shape: rules
    /// declared inline per row, with a second collection nested inside judged the same way.
    /// The Nested branch is the harshest case: its holding rule is untagged, so under the root
    /// adaptor's ["Admin"] selector the whole subtree executes under no profile at all.
    /// </summary>
    private sealed class EquivalenceValidator : AbstractValidator<EquivalenceModel>
    {
        public EquivalenceValidator()
        {
            RuleFor(m => m.Plain).NotEmpty();
            RuleSet("Submit", () => RuleFor(m => m.SubmitTagged).NotEmpty());
            RuleSet("Admin", () => RuleFor(m => m.AdminTagged).NotEmpty());
            RuleSet("Submit,Engaged", () => RuleFor(m => m.CommaTagged).NotEmpty());
            Include(new IncludedEquivalencePart());
            RuleSet("Engaged", () => Include(new EngagedEquivalencePart()));
            RuleFor(m => m.Unscoped).SetValidator(new EquivalenceChildValidator());
            RuleFor(m => m.ScopedAdmin).SetValidator(new EquivalenceChildValidator(), "Admin");
            RuleFor(m => m.ScopedAdminDefault).SetValidator(new EquivalenceChildValidator(), "Admin", "default");
            RuleFor(m => m.ScopedWildcard).SetValidator(new EquivalenceChildValidator(), "*");
            RuleFor(m => m.Bundled).ChildRules(c => c.RuleFor(x => x.City).NotEmpty());
            RuleFor(m => m.Nested).SetValidator(new EquivalenceNestValidator(), "Admin");
            RuleForEach(m => m.Sites).SetValidator(new EquivalenceChildValidator(), "Admin");
            RuleForEach(m => m.Teams).ChildRules(team =>
            {
                team.RuleFor(t => t.Name).NotEmpty();
                team.RuleForEach(t => t.Members).ChildRules(member => member.RuleFor(c => c.City).NotEmpty());
            });
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
    /// rule, not silence, which the declared paths alongside it show.
    /// </summary>
    [Fact]
    public void The_profile_decides_whether_a_field_is_required()
    {
        var adapter = new FluentValidationModelValidator<DraftedBrief>(new DraftedBriefValidator());

        Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Title", ValidationProfile.Submit));
        Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Summary", ValidationProfile.Submit));
        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Title", ValidationProfile.Draft));
        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Summary", ValidationProfile.Draft));

        // Title's length rule rides the draft bucket, so the field is declared under Draft and
        // simply not required. Summary's only rule is the submit-bucket presence rule, so the
        // draft profile reaches no component of it at all.
        var draftPaths = adapter.GetDeclaredFieldPaths(ValidationProfile.Draft);
        Assert.Contains("Title", draftPaths);
        Assert.DoesNotContain("Summary", draftPaths);
    }

    /// <summary>
    /// A presence rule reached only through a condition answers conditionally required, for
    /// every place a condition on a single-member rule can be recorded — a When chained after
    /// the component, a When block wrapping the rule declaration, the CurrentValidator form,
    /// the asynchronous form, and Unless, which FluentValidation records as a negated When. (A
    /// collection rule can also carry a per-row Where filter, pinned with the row pins.)
    /// Reading the rule-level flag alone reports four of the five as unconditional.
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
        Assert.DoesNotContain("Nothing", adapter.GetDeclaredFieldPaths(ValidationProfile.Draft));
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
        Assert.Contains("Title", adapter.GetDeclaredFieldPaths(ValidationProfile.Draft));
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
        Assert.Empty(adapter.GetDeclaredFieldPaths(ValidationProfile.Draft));
    }

    /// <summary>
    /// The declared paths are the validator's own shape with every index left open: a rule
    /// declared per collection element is listed under the collection with <c>[]</c> where the
    /// index goes, nesting chains, and a collection carrying no rule of its own is absent while
    /// one carrying a rule is present. This is the whole enumeration, asserted as a set, because
    /// what it must not do is quietly gain or lose a path.
    /// </summary>
    [Fact]
    public void The_declared_paths_are_the_validators_own_shape_with_indexes_left_open()
    {
        var adapter = new FluentValidationModelValidator<EventRegistration>(new EventRegistrationValidator());

        Assert.Equal(
            [
                "Attendees",
                "Attendees[].Email",
                "Attendees[].Name",
                "CateringHeadcount",
                "ContactEmail",
                "DietaryNotes",
                "EarlyBirdDeadline",
                "EventDate",
                "EventName",
                "Sessions[].Seats",
                "TicketTier",
                "VenueRegion",
            ],
            adapter.GetDeclaredFieldPaths(ValidationProfile.Submit).OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>
    /// Nesting chains, and the answer covers a path no failure-side reading could produce:
    /// Members is a collection inside a collection, and its own presence rule is declared inside
    /// the child validator that judges each team.
    /// </summary>
    [Fact]
    public void Declared_paths_nest_through_a_collection_inside_a_collection()
    {
        var adapter = new FluentValidationModelValidator<Roster>(new RosterValidator());

        Assert.Equal(
            ["Teams", "Teams[].Members", "Teams[].Members[].Alias", "Teams[].Name"],
            adapter.GetDeclaredFieldPaths(ValidationProfile.Submit).OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>
    /// A child's paths are gone from the answer when the profile does not select the rule that
    /// CARRIES the child: the attendee rules hang off a submit-bucket <c>RuleForEach</c>, so a
    /// draft profile reaches neither them nor the top-level submit-bucket fields, while the
    /// draft-bucket session rule stays. Selection applied at the child's own level as well is a
    /// separate property, and the ruleset-scoped pin below is what discriminates it — lifting
    /// the child-level check alone leaves this test passing, because the parent's own tag
    /// already excludes these paths.
    /// </summary>
    [Fact]
    public void A_child_is_unread_when_the_profile_skips_the_rule_carrying_it()
    {
        var adapter = new FluentValidationModelValidator<EventRegistration>(new EventRegistrationValidator());

        var draft = adapter.GetDeclaredFieldPaths(ValidationProfile.Draft);

        Assert.Contains("Sessions[].Seats", draft);
        Assert.DoesNotContain("Attendees[].Name", draft);
        Assert.DoesNotContain("EventName", draft);
    }

    /// <summary>
    /// The requirement answer and the declared-path answer are one walk, so a templated path is
    /// answerable — and so is the indexed path a failure or a bound row field carries, which is
    /// answered by normalising each index to the open form the declaring template uses. The
    /// answer is a property of the rules rather than of any model, so an index no model in hand
    /// could reach answers the same way — inspection has no model to bound it against.
    /// </summary>
    [Fact]
    public void A_rule_declared_per_row_answers_under_its_templated_and_indexed_paths()
    {
        var adapter = new FluentValidationModelValidator<EventRegistration>(new EventRegistrationValidator());

        Assert.Equal(
            RuleRequirement.Required,
            adapter.GetFieldRequirement("Attendees[].Name", ValidationProfile.Submit));
        Assert.Equal(
            RuleRequirement.Required,
            adapter.GetFieldRequirement("Attendees[0].Name", ValidationProfile.Submit));
        Assert.Equal(
            RuleRequirement.Required,
            adapter.GetFieldRequirement("Attendees[57].Name", ValidationProfile.Submit));
    }

    /// <summary>
    /// Normalisation reaches every level of a nested path: each bracketed index is rewritten to
    /// the open form, so a path indexed at two depths lands on the template declared inside the
    /// child validator that judges each team's members, and a singly-indexed path lands on the
    /// team-level template beside it.
    /// </summary>
    [Fact]
    public void An_indexed_path_normalises_to_its_template_at_every_level()
    {
        var adapter = new FluentValidationModelValidator<Roster>(new RosterValidator());

        Assert.Equal(
            RuleRequirement.Required,
            adapter.GetFieldRequirement("Teams[0].Members[1].Alias", ValidationProfile.Submit));
        Assert.Equal(
            RuleRequirement.Required,
            adapter.GetFieldRequirement("Teams[2].Name", ValidationProfile.Submit));
    }

    /// <summary>
    /// A per-row filter is a condition the walk cannot evaluate — which rows it admits is
    /// unknowable without a model — so a filtered rule's demands answer conditionally
    /// required, template and indexed rows alike, exactly as a When on the carrying rule
    /// does; the asynchronous form answers the same way. The unfiltered shape answering
    /// Required is pinned above, which is what makes this a reading of the filter rather
    /// than of RuleForEach itself.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_row_filtered_rule_answers_conditionally_required_for_template_and_rows(bool asyncFilter)
    {
        FluentValidationModelValidator<InspectModel> adapter = asyncFilter
            ? new(new RowAsyncFilteredValidator())
            : new(new RowFilteredValidator());

        Assert.Equal(
            RuleRequirement.ConditionallyRequired,
            adapter.GetFieldRequirement("Rows[].City", ValidationProfile.Draft));
        Assert.Equal(
            RuleRequirement.ConditionallyRequired,
            adapter.GetFieldRequirement("Rows[0].City", ValidationProfile.Draft));
    }

    /// <summary>
    /// A condition on the rule carrying a collection's child validator travels down into the
    /// template, and the template's answer is every row's answer — the condition was declared
    /// about the carrying rule, so no index can hold a different one.
    /// </summary>
    [Fact]
    public void A_row_conditional_rule_answers_conditionally_required_for_every_row()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new RowConditionalValidator());

        Assert.Equal(
            RuleRequirement.ConditionallyRequired,
            adapter.GetFieldRequirement("Rows[].City", ValidationProfile.Draft));
        Assert.Equal(
            RuleRequirement.ConditionallyRequired,
            adapter.GetFieldRequirement("Rows[0].City", ValidationProfile.Draft));
        Assert.Equal(
            RuleRequirement.ConditionallyRequired,
            adapter.GetFieldRequirement("Rows[7].City", ValidationProfile.Draft));
    }

    /// <summary>
    /// A rule merged in with Include() lands at the including validator's own level, because
    /// that is where its failures land. Reading only the rules the validator declares for itself
    /// drops it — and the field then reports as not required however plainly it is.
    /// </summary>
    [Fact]
    public void An_included_validators_rules_merge_at_the_including_level()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new IncludingValidator());

        Assert.Equal(
            ["Chained", "Title"],
            adapter.GetDeclaredFieldPaths(ValidationProfile.Draft).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Chained", ValidationProfile.Draft));
    }

    /// <summary>
    /// A model-level rule carrying a child validator merges that child at the ROOT's own level,
    /// because that is where its failures land — the third route by which a rule the root does
    /// not declare for itself is still read. Both spellings behave alike: a validator for the
    /// model's own type, and the <c>ChildRules</c> block that wraps one.
    /// </summary>
    [Theory]
    [InlineData(nameof(ModelLevelChildValidator))]
    [InlineData(nameof(ModelLevelChildRulesValidator))]
    public void A_child_validator_a_model_level_rule_carries_merges_at_the_root(string shape)
    {
        var validator = shape == nameof(ModelLevelChildValidator)
            ? new FluentValidationModelValidator<InspectModel>(new ModelLevelChildValidator())
            : new FluentValidationModelValidator<InspectModel>(new ModelLevelChildRulesValidator());

        Assert.Equal(["Chained"], validator.GetDeclaredFieldPaths(ValidationProfile.Draft));
        Assert.Equal(RuleRequirement.Required, validator.GetFieldRequirement("Chained", ValidationProfile.Draft));
    }

    /// <summary>
    /// The two spellings of a scoped child both agree with execution. Scoping by the
    /// <c>SetValidator</c> call leaves the child's untagged rule selected under no profile —
    /// the replacing selector carries no default bucket — while scoping by tagging the child's
    /// OWN rules keeps selection under the profile's selector, which a scope-less
    /// <c>SetValidator</c> call passes down: silent under Draft, demanded under a profile
    /// naming "Admin". Each answer is checked against the run it predicts.
    /// </summary>
    [Fact]
    public async Task A_ruleset_scoped_child_and_a_self_scoping_one_are_both_read_exactly()
    {
        var scoped = new FluentValidationModelValidator<InspectModel>(new RuleSetScopedChildValidator());
        var admin = ValidationProfile.Named("Admin", includeDefaultRules: true, "Admin");

        Assert.Equal(RuleRequirement.NotRequired, scoped.GetFieldRequirement("Child.City", ValidationProfile.Draft));
        Assert.Empty((await scoped.ValidateAsync(new InspectModel(), ValidationProfile.Draft)).Issues);

        Assert.Equal(RuleRequirement.NotRequired, scoped.GetFieldRequirement("Child.City", admin));
        Assert.Empty((await scoped.ValidateAsync(new InspectModel(), admin)).Issues);

        var selfScoping = new FluentValidationModelValidator<InspectModel>(new SelfScopingChildRootValidator());

        Assert.Equal(RuleRequirement.NotRequired, selfScoping.GetFieldRequirement("Child.City", ValidationProfile.Draft));
        Assert.Empty((await selfScoping.ValidateAsync(new InspectModel(), ValidationProfile.Draft)).Issues);

        Assert.Equal(RuleRequirement.Required, selfScoping.GetFieldRequirement("Child.City", admin));
        Assert.Equal(
            ["Child.City"],
            (await selfScoping.ValidateAsync(new InspectModel(), admin)).Issues.Select(i => i.Path));
    }

    /// <summary>
    /// A child scoped by its <c>SetValidator</c> call runs under a selector built from the
    /// call's rulesets alone — replacing the profile's, never intersecting with it — so an
    /// untagged rule inside such a child is selected under no profile at all: the replacing
    /// selector carries no default bucket, and the profile's names never reach the child's own
    /// selection, "Admin" included. The runs are the ground truth for both profiles.
    /// </summary>
    [Fact]
    public async Task An_untagged_child_rule_under_a_scoped_adaptor_is_selected_under_no_profile()
    {
        var scoped = new FluentValidationModelValidator<InspectModel>(new RuleSetScopedChildValidator());
        var admin = ValidationProfile.Named("Admin", includeDefaultRules: true, "Admin");

        Assert.Equal(RuleRequirement.NotRequired, scoped.GetFieldRequirement("Child.City", ValidationProfile.Submit));
        Assert.Equal(RuleRequirement.NotRequired, scoped.GetFieldRequirement("Child.City", admin));
        Assert.Empty((await scoped.ValidateAsync(new InspectModel(), ValidationProfile.Submit)).Issues);
        Assert.Empty((await scoped.ValidateAsync(new InspectModel(), admin)).Issues);
    }

    /// <summary>
    /// The same replacement read from the other side: a child rule tagged into the ruleset the
    /// <c>SetValidator</c> call names is selected under every profile that selects the untagged
    /// holding rule — Submit included, though Submit never names "Admin" — and under none that
    /// skips the holding rule. Reporting it not required under Submit would silently drop a
    /// demand every submit run enforces.
    /// </summary>
    [Fact]
    public async Task A_tagged_child_rule_under_a_scoped_adaptor_follows_the_adaptors_scope_not_the_profiles()
    {
        var scoped = new FluentValidationModelValidator<InspectModel>(new ScopedTaggedChildRootValidator());
        var adminOnly = ValidationProfile.Named("AdminOnly", includeDefaultRules: false, "Admin");

        Assert.Equal(RuleRequirement.Required, scoped.GetFieldRequirement("Child.City", ValidationProfile.Submit));
        Assert.Equal(
            ["Child.City"],
            (await scoped.ValidateAsync(new InspectModel(), ValidationProfile.Submit)).Issues.Select(i => i.Path));

        Assert.Equal(RuleRequirement.NotRequired, scoped.GetFieldRequirement("Child.City", adminOnly));
        Assert.Empty((await scoped.ValidateAsync(new InspectModel(), adminOnly)).Issues);
    }

    /// <summary>
    /// A wildcard among the profile's rulesets admits every rule the profile's own selector is
    /// asked about — the holding rules among them — but a scoped child's replacing selector is
    /// a different selector, and it is never asked with the wildcard: the untagged child rule
    /// stays unselected under the adaptor's own names, the tagged one is selected by them, and
    /// the wildcard runs agree on both.
    /// </summary>
    [Fact]
    public async Task A_wildcard_profile_reports_exactly_what_a_wildcard_run_executes()
    {
        var allOnly = ValidationProfile.Named("AllOnly", includeDefaultRules: false, "*");

        var scoped = new FluentValidationModelValidator<InspectModel>(new RuleSetScopedChildValidator());
        Assert.Equal(RuleRequirement.NotRequired, scoped.GetFieldRequirement("Child.City", allOnly));
        Assert.Empty((await scoped.ValidateAsync(new InspectModel(), allOnly)).Issues);

        var tagged = new FluentValidationModelValidator<InspectModel>(new ScopedTaggedChildRootValidator());
        Assert.Equal(RuleRequirement.Required, tagged.GetFieldRequirement("Child.City", allOnly));
        Assert.Equal(
            ["Child.City"],
            (await tagged.ValidateAsync(new InspectModel(), allOnly)).Issues.Select(i => i.Path));
    }

    /// <summary>
    /// Selector replacement applies at every child boundary, not the first alone: the root
    /// adaptor's ["Admin"] selects the outer validator's tagged holding rule, and the inner
    /// adaptor's ["Special"] selects the child's tagged rule while leaving its untagged
    /// sibling unselected. The run confirms exactly one demand survives the two boundaries.
    /// </summary>
    [Fact]
    public async Task A_scoped_child_inside_a_scoped_child_answers_at_both_depths()
    {
        var adapter = new FluentValidationModelValidator<NestedScopeModel>(new NestedScopedRootValidator());

        Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Outer.Inner.City", ValidationProfile.Draft));
        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Outer.Inner.Zip", ValidationProfile.Draft));
        Assert.Equal(
            ["Outer.Inner.City"],
            (await adapter.ValidateAsync(new NestedScopeModel(), ValidationProfile.Draft)).Issues.Select(i => i.Path));
    }

    /// <summary>
    /// An adaptor carrying no rulesets inherits the selector in force — an absent scope means
    /// "no replacement", never "an empty selection". <c>ChildRules</c> is the shape that would
    /// break under the empty reading: it scopes nothing on its adaptor and instead tags the
    /// child's own rules with the declaring scope's rulesets, so those tags must be matched
    /// against the profile's selector for the child's rules to be selected at all.
    /// </summary>
    [Fact]
    public async Task A_child_carrying_no_rulesets_keeps_the_selector_in_force()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new TaggedChildRulesValidator());

        Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Child.City", ValidationProfile.Submit));
        Assert.Equal(
            ["Child.City"],
            (await adapter.ValidateAsync(new InspectModel(), ValidationProfile.Submit)).Issues.Select(i => i.Path));

        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Child.City", ValidationProfile.Draft));
        Assert.Empty((await adapter.ValidateAsync(new InspectModel(), ValidationProfile.Draft)).Issues);
    }

    /// <summary>
    /// The root selector is the global factory's, so a consumer who replaces
    /// <c>ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory</c> moves
    /// a run's selection and inspection's answer together: a selector admitting probe-tagged
    /// rules regardless of the name list makes a Draft run fail the tagged rule, and
    /// inspection reports it required under the same profile. A root selector built directly
    /// would leave inspection blind to a replacement the run follows.
    /// </summary>
    [Fact]
    public async Task A_replaced_global_selector_factory_moves_inspection_with_the_run()
    {
        var stock = ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory;
        try
        {
            ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory =
                ruleSets => new FactoryProbeSelector(ruleSets);

            var adapter = new FluentValidationModelValidator<InspectModel>(new FactoryProbeValidator());

            var run = await adapter.ValidateAsync(new InspectModel(), ValidationProfile.Draft);
            Assert.Contains("Chained", run.Issues.Select(i => i.Path));
            Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Chained", ValidationProfile.Draft));
            Assert.Contains("Chained", adapter.GetDeclaredFieldPaths(ValidationProfile.Draft));
        }
        finally
        {
            ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory = stock;
        }
    }

    /// <summary>
    /// Equivalence with execution, measured rather than expected: every rule in the harness is
    /// a presence rule, so a run over an all-empty model fails exactly what it selects. What
    /// inspection reports as required must equal what the run failed, path for path, with no
    /// claim in either direction — the two directions are asserted separately so a failure
    /// names its paths as over-claims or under-claims.
    /// </summary>
    [Theory]
    [InlineData("Draft")]
    [InlineData("Submit")]
    [InlineData("Admin")]
    [InlineData("AdminOnly")]
    [InlineData("Engaged")]
    [InlineData("EngagedOnly")]
    [InlineData("All")]
    [InlineData("AllOnly")]
    [InlineData("DefaultNamed")]
    [InlineData("Mixed")]
    public async Task The_reported_set_equals_what_a_real_run_executes(string profileName)
    {
        var profile = profileName switch
        {
            "Draft" => ValidationProfile.Draft,
            "Submit" => ValidationProfile.Submit,
            "Admin" => ValidationProfile.Named("Admin", includeDefaultRules: true, "Admin"),
            "AdminOnly" => ValidationProfile.Named("AdminOnly", includeDefaultRules: false, "Admin"),
            "Engaged" => ValidationProfile.Named("Engaged", includeDefaultRules: true, "Engaged"),
            "EngagedOnly" => ValidationProfile.Named("EngagedOnly", includeDefaultRules: false, "Engaged"),
            "All" => ValidationProfile.Named("All", includeDefaultRules: true, "*"),
            "AllOnly" => ValidationProfile.Named("AllOnly", includeDefaultRules: false, "*"),
            "DefaultNamed" => ValidationProfile.Named("DefaultNamed", includeDefaultRules: false, "default"),
            "Mixed" => ValidationProfile.Named("Mixed", includeDefaultRules: false, "Submit", "Admin"),
            _ => throw new ArgumentOutOfRangeException(nameof(profileName)),
        };
        var adapter = new FluentValidationModelValidator<EquivalenceModel>(new EquivalenceValidator());

        var executed = (await adapter.ValidateAsync(new EquivalenceModel(), profile))
            .Issues.Select(i => Templated(i.Path))
            .ToHashSet(StringComparer.Ordinal);
        var claimed = adapter.GetDeclaredFieldPaths(profile)
            .Where(path => adapter.GetFieldRequirement(path, profile) == RuleRequirement.Required)
            .ToHashSet(StringComparer.Ordinal);

        // Every profile here selects at least one rule, so the comparison is never vacuous.
        Assert.NotEmpty(executed);
        Assert.Empty(claimed.Except(executed).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Empty(executed.Except(claimed).OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>
    /// Both the child resolve and the scoped-boundary selector replacement read
    /// <c>ChildValidatorAdaptor&lt;,&gt;.RuleSets</c>, a public property FluentValidation's
    /// published XML docs do not mention. If an upgrade removes or hides it, scoped children
    /// would silently be read under the selector in force at their boundary — this fails by
    /// name instead.
    /// </summary>
    [Fact]
    public void FluentValidation_still_exposes_the_child_adaptors_ruleset_scope()
    {
        var ruleSets = typeof(ChildValidatorAdaptor<,>).GetProperty("RuleSets", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(ruleSets);
        Assert.Equal(typeof(string[]), ruleSets.PropertyType);
        Assert.NotNull(ruleSets.GetMethod);
    }

    /// <summary>
    /// The row-filter read finds <c>ICollectionRule&lt;,&gt;.Filter</c> and <c>AsyncFilter</c>
    /// by name, and a failed read deliberately answers "unfiltered" — so if an upgrade removed
    /// or renamed either property, a <c>Where</c>-filtered rule would silently report
    /// <c>Required</c>: a false mark in the one direction that never self-corrects. This fails
    /// by name instead.
    /// </summary>
    [Fact]
    public void FluentValidation_still_exposes_the_collection_rules_row_filters()
    {
        var closed = typeof(ICollectionRule<,>).MakeGenericType(typeof(EquivalenceModel), typeof(EquivalenceChild));

        var filter = closed.GetProperty("Filter", BindingFlags.Public | BindingFlags.Instance);
        var asyncFilter = closed.GetProperty("AsyncFilter", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(filter);
        Assert.NotNull(filter.GetMethod);
        Assert.NotNull(asyncFilter);
        Assert.NotNull(asyncFilter.GetMethod);
    }

    /// <summary>
    /// A condition travels down: a child validator reached only through a conditional rule
    /// demands its own fields conditionally, however unconditionally the child declares them.
    /// Reading the child's own flags alone reports it as unconditionally required.
    /// </summary>
    [Fact]
    public void A_condition_on_the_parent_rule_reaches_the_child_validators_fields()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new ConditionalChildValidator());

        Assert.Equal(
            RuleRequirement.ConditionallyRequired,
            adapter.GetFieldRequirement("Child.City", ValidationProfile.Draft));
    }

    /// <summary>
    /// A child validator produced by a lambda needs a model to exist, and inspection has none —
    /// so its paths are absent rather than guessed at. The sibling rule in the same validator is
    /// still read, so this pins "that one child is unreadable" rather than "the walk gave up".
    /// </summary>
    [Fact]
    public void A_child_validator_whose_lambda_needs_the_model_is_not_read()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new LambdaChildValidator());

        Assert.Equal(
            ["Title"],
            adapter.GetDeclaredFieldPaths(ValidationProfile.Draft).OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>
    /// A validator that includes itself is walked once, so the answer is finite and carries no
    /// path that describes a nesting depth nothing declared. The bound is the walk's own path,
    /// not a depth cap: what stops it is meeting the same validator again.
    /// </summary>
    [Fact]
    public void A_validator_that_includes_itself_is_walked_once()
    {
        var adapter = new FluentValidationModelValidator<InspectNode>(new InspectNodeValidator());

        Assert.Equal(
            ["Label"],
            adapter.GetDeclaredFieldPaths(ValidationProfile.Draft).OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>
    /// A rule declared per element of a collection whose components judge each element directly
    /// is read under the collection's own path — there is no child validator to descend into, so
    /// nothing about the element is declared separately. The indexed path a failure carries is
    /// no answer for this shape either: it normalises to <c>Tags[]</c>, and no rule declares
    /// that template.
    /// </summary>
    [Fact]
    public void A_collection_rule_answers_under_the_collection_path_not_the_indexed_one()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new CollectionRuleValidator());

        Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Tags", ValidationProfile.Draft));
        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Tags[0]", ValidationProfile.Draft));

        Assert.Equal(["Tags"], adapter.GetDeclaredFieldPaths(ValidationProfile.Draft));
    }

    /// <summary>
    /// A child validator's rules are declared under the child's own path, and the field carrying
    /// the child is not itself declared — its failures belong to the child's members, so filing
    /// the parent field would attribute the child's complaint to it.
    /// </summary>
    [Fact]
    public void A_child_validators_rules_land_under_the_childs_own_path()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new CodeMapValidator());

        var declared = adapter.GetDeclaredFieldPaths(ValidationProfile.Draft);

        Assert.Contains("Child.City", declared);
        Assert.DoesNotContain("Child", declared);
        Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Child.City", ValidationProfile.Draft));
    }

    /// <summary>
    /// A field with rules but no presence rule is still declared: "this form has something to
    /// say about this field" and "this field must carry a value" are different questions, and a
    /// caller enumerating the fields a form speaks about needs the first one answered for both.
    /// </summary>
    [Fact]
    public void A_field_with_rules_but_no_presence_rule_is_declared_and_not_required()
    {
        var adapter = new FluentValidationModelValidator<InspectModel>(new ShapeValidator());

        Assert.Contains("Predicate", adapter.GetDeclaredFieldPaths(ValidationProfile.Draft));
        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Predicate", ValidationProfile.Draft));
    }

    /// <summary>
    /// The declared paths are the paths real failures carry, once each index is replaced by the
    /// open form the templates use. This is what lets a caller holding only a report decide that
    /// a failing field is one the form declares rules for.
    /// </summary>
    [Fact]
    public async Task The_declared_paths_are_the_paths_the_failures_carry()
    {
        var adapter = new FluentValidationModelValidator<Roster>(new RosterValidator());
        var declared = adapter.GetDeclaredFieldPaths(ValidationProfile.Submit);

        var roster = new Roster { Teams = { new Team { Members = { new Member() } } } };
        var report = await adapter.ValidateAsync(roster, ValidationProfile.Submit);

        Assert.NotEmpty(report.Issues);
        foreach (var issue in report.Issues)
        {
            Assert.Contains(Templated(issue.Path), declared);
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
        Assert.Throws<InvalidOperationException>(() => adapter.GetDeclaredFieldPaths(profile));

        // And it throws every time it is asked, not only the first: nothing that failed
        // verification is ever remembered, so a repeat ask cannot be served an answer the
        // ruleset check never let anyone compute.
        Assert.Throws<InvalidOperationException>(() => adapter.GetFieldRequirement("Title", profile));
        Assert.Throws<InvalidOperationException>(() => adapter.GetDeclaredFieldPaths(profile));
    }

    /// <summary>
    /// Two profiles carrying the same NAME and different rulesets are different questions, and
    /// each gets its own answer. Profiles compare by name, so anything holding an answer against
    /// a profile has to hold it against the instance rather than against equality — otherwise
    /// the second ask here is served the first one's answer.
    /// </summary>
    [Fact]
    public void Two_profiles_sharing_a_name_do_not_share_an_answer()
    {
        var adapter = new FluentValidationModelValidator<DraftedBrief>(new DraftedBriefValidator());
        var draftOnly = ValidationProfile.Named("Same", includeDefaultRules: true);
        var withSubmit = ValidationProfile.Named("Same", includeDefaultRules: true, ValidationProfile.SubmitRuleSetName);

        Assert.Equal(draftOnly, withSubmit); // equality is by name: the trap this guards
        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Title", draftOnly));
        Assert.Equal(RuleRequirement.Required, adapter.GetFieldRequirement("Title", withSubmit));
        Assert.Equal(RuleRequirement.NotRequired, adapter.GetFieldRequirement("Title", draftOnly));
    }
}
