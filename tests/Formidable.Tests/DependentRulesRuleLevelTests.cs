using FluentValidation;
using FluentValidation.Internal;
using Xunit.Abstractions;

namespace Formidable.Tests;

/// <summary>
/// A set of rules run apart runs the rules inside their <c>DependentRules</c> as validating the
/// whole profile does, wherever a dependent rule sits, and the reading of the rules files each
/// dependent rule under its own field.
/// </summary>
// The mutations these tests name, each run against the adapter:
// (1) refuse a dependent in RuleSetSelector.CanExecute (return false where it is delegated), and
//     every shape with a dependent on one of the validator's own rules answers without it;
// (2) drop the profile-scope test for a dependent (skip ChildDecisionIsProfileIndependent for it),
//     and a dependent in a ruleset its carrying rule is not in reads as answering every profile;
// (3) drop the DependentRules walk from WalkDeclaredRule, and no dependent's field is declared;
// (4) make EveryFailureBlocks answer true, and a dependent under a rule that can fail as a warning
//     or through a child validator reads Required;
// (5) drop the every-check-conditional clause for a rule on each row from WalkDeclaredRule, and a
//     dependent under such a rule reads Required;
// (6) drop the DependentRules test from SelectionFollowsMoreThanMembership, and a rule carrying
//     dependents shares a group with its untagged siblings, which its scope then marks;
// (7) let a row filter on the carrying rule make its dependents conditional in WalkDeclaredRule,
//     and a dependent under a filtered rule on each row reads ConditionallyRequired.
public class DependentRulesRuleLevelTests(ITestOutputHelper output)
{
    private const string DependentMessage = "Customer name is required";

    private const string SkippedEmail = "skip@example.com";

    public sealed class Customer
    {
        public string? Name { get; set; } = string.Empty;

        public string Email { get; set; } = "ada@example.com";
    }

    public sealed class Order
    {
        public string Description { get; set; } = "Quarterly plan";

        public string Reference { get; set; } = "Q3";

        public Customer Customer { get; set; } = new();

        public List<Customer> Lines { get; set; } = [new()];
    }

    /// <summary>The customer's name is checked once the description is present.</summary>
    private sealed class DependentOrderValidator : AbstractValidator<Order>
    {
        public DependentOrderValidator() =>
            RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage));
    }

    /// <summary>A child validator whose own rule carries the dependent rule.</summary>
    private sealed class DependentCustomerValidator : AbstractValidator<Customer>
    {
        public DependentCustomerValidator() =>
            RuleFor(c => c.Email).NotEmpty().DependentRules(() =>
                RuleFor(c => c.Name).NotEmpty().WithMessage(DependentMessage));
    }

    /// <summary>A child validator whose dependent rule sits in the Submit ruleset under an untagged rule.</summary>
    private sealed class TaggedDependentCustomerValidator : AbstractValidator<Customer>
    {
        public TaggedDependentCustomerValidator() =>
            RuleFor(c => c.Email).NotEmpty().DependentRules(() =>
                RuleSet("Submit", () => RuleFor(c => c.Name).NotEmpty().WithMessage(DependentMessage)));
    }

    /// <summary>A child validator with no dependent rule, for a dependent chained onto the rule that attaches it.</summary>
    private sealed class EmailValidator : AbstractValidator<Customer>
    {
        public EmailValidator() => RuleFor(c => c.Email).NotEmpty();
    }

    /// <summary>A child validator, attached inside DependentRules, with an untagged rule and one in the Submit ruleset.</summary>
    private sealed class TaggedNameCustomerValidator : AbstractValidator<Customer>
    {
        public TaggedNameCustomerValidator()
        {
            RuleFor(c => c.Email).NotEmpty();
            RuleSet("Submit", () => RuleFor(c => c.Name).NotEmpty().WithMessage(DependentMessage));
        }
    }

    /// <summary>A validator of the whole order with one untagged rule, included inside DependentRules.</summary>
    private sealed class NameOnlyOrderValidator : AbstractValidator<Order>
    {
        public NameOnlyOrderValidator() => RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage);
    }

    public enum Shape
    {
        None,
        EmptyDependents,
        TopLevel,
        Nested,
        OnRuleForEach,
        WhenAround,
        OtherwiseAround,
        WhenInsideDependents,
        OnSetValidatorRule,
        InRuleSet,
        TaggedUnderUntagged,
        RuleForEachInRuleSet,
        UnselectedRuleSetDependent,
        InChild,
        InChildScoped,
        InChildRules,
        InInclude,
        LambdaChild,
        IncludeInRuleSet,
        ChildInRuleSetForEach,
        ChildTaggedUnderUntagged,
        WarningOwner,
        DeclaredOutsideItsRuleSet,
        ForEachEveryCheckConditional,
        ForEachOneCheckUnconditional,
        SiblingsOfTaggedUnderUntagged,
        ChildInsideDependents,
        IncludeInsideDependents,
        ForEachInsideDependents,
        ForEachRowFilter,
        ForEachAsyncRowFilter,
    }

    public enum State
    {
        DependentsFail,
        OwnersFail,
        AllPass,
    }

    private static InlineValidator<Order> Build(Shape shape)
    {
        var v = new InlineValidator<Order>();
        switch (shape)
        {
            case Shape.None:
                v.RuleFor(x => x.Description).NotEmpty();
                v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage);
                break;
            case Shape.EmptyDependents:
                v.RuleFor(x => x.Description).NotEmpty().DependentRules(() => { });
                v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage);
                break;
            case Shape.TopLevel:
                v.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                    v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage));
                break;
            case Shape.Nested:
                v.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                    v.RuleFor(x => x.Reference).NotEmpty().DependentRules(() =>
                        v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
                break;
            case Shape.OnRuleForEach:
                v.RuleForEach(x => x.Lines).NotNull().DependentRules(() =>
                    v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage));
                break;
            case Shape.WhenAround:
                v.When(x => x.Reference.Length > 0, () =>
                    v.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                        v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
                break;
            case Shape.OtherwiseAround:
                v.When(x => x.Reference.Length == 0, () => { }).Otherwise(() =>
                    v.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                        v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
                break;
            case Shape.WhenInsideDependents:
                v.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                    v.When(x => x.Reference.Length > 0, () =>
                        v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
                break;
            case Shape.OnSetValidatorRule:
                v.RuleFor(x => x.Customer).SetValidator(new EmailValidator()).DependentRules(() =>
                    v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage));
                break;
            case Shape.InRuleSet:
                v.RuleSet("Submit", () =>
                    v.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                        v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
                break;
            case Shape.TaggedUnderUntagged:
                v.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                    v.RuleSet("Submit", () =>
                        v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
                break;
            case Shape.RuleForEachInRuleSet:
                v.RuleSet("Submit", () =>
                    v.RuleForEach(x => x.Lines).NotNull().DependentRules(() =>
                        v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
                break;
            case Shape.UnselectedRuleSetDependent:
                v.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                    v.RuleSet("Admin", () =>
                        v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
                break;
            case Shape.InChild:
                v.RuleFor(x => x.Customer).SetValidator(new DependentCustomerValidator());
                break;
            case Shape.InChildScoped:
                v.RuleFor(x => x.Customer).SetValidator(new DependentCustomerValidator(), "default");
                break;
            case Shape.InChildRules:
                v.RuleFor(x => x.Customer).ChildRules(c =>
                    c.RuleFor(cc => cc.Email).NotEmpty().DependentRules(() =>
                        c.RuleFor(cc => cc.Name).NotEmpty().WithMessage(DependentMessage)));
                break;
            case Shape.InInclude:
                v.Include(new DependentOrderValidator());
                break;
            case Shape.LambdaChild:
                v.RuleFor(x => x.Customer).SetValidator(_ => new DependentCustomerValidator());
                break;
            case Shape.IncludeInRuleSet:
                v.RuleSet("Submit", () => v.Include(new DependentOrderValidator()));
                break;
            case Shape.ChildInRuleSetForEach:
                v.RuleSet("Submit", () => v.RuleForEach(x => x.Lines).SetValidator(new DependentCustomerValidator()));
                break;
            case Shape.ChildTaggedUnderUntagged:
                v.RuleFor(x => x.Customer).SetValidator(new TaggedDependentCustomerValidator());
                break;
            case Shape.WarningOwner:
                v.RuleFor(x => x.Description).NotEmpty().WithSeverity(Severity.Warning).DependentRules(() =>
                    v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage));
                break;
            case Shape.DeclaredOutsideItsRuleSet:
                // DependentRules called after the RuleSet block has closed, so no block tags the
                // rules inside it: Reference takes the carrying rule's ruleset, and Customer.Name,
                // one level further down, takes none.
                IRuleBuilderOptions<Order, string>? carrying = null;
                v.RuleSet("Submit", () => carrying = v.RuleFor(x => x.Description).NotEmpty());
                carrying!.DependentRules(() =>
                    v.RuleFor(x => x.Reference).NotEmpty().DependentRules(() =>
                        v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
                break;
            case Shape.ForEachEveryCheckConditional:
                // While the condition is off no check of the rule on each row runs, and
                // FluentValidation then returns before the rules inside DependentRules.
                v.RuleForEach(x => x.Lines).NotNull().When(x => x.Reference.Length > 0).DependentRules(() =>
                    v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage));
                break;
            case Shape.ForEachOneCheckUnconditional:
                // The Must after the When carries no condition, so the rule on each row always runs.
                v.RuleForEach(x => x.Lines).NotNull().When(x => x.Reference.Length > 0).Must(line => line is not null)
                    .DependentRules(() => v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage));
                break;
            case Shape.SiblingsOfTaggedUnderUntagged:
                v.RuleFor(x => x.Reference).NotEmpty();
                v.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                    v.RuleSet("Submit", () =>
                        v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
                v.RuleFor(x => x.Customer.Email).NotEmpty();
                break;
            case Shape.ChildInsideDependents:
                v.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                    v.RuleFor(x => x.Customer).SetValidator(new TaggedNameCustomerValidator()));
                break;
            case Shape.IncludeInsideDependents:
                v.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                    v.Include(new NameOnlyOrderValidator()));
                break;
            case Shape.ForEachInsideDependents:
                v.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                    v.RuleForEach(x => x.Lines).NotNull());
                break;
            case Shape.ForEachRowFilter:
                // A row filter skips rows; FluentValidation still runs the rules inside
                // DependentRules after the rows when no row added a failure.
                v.RuleForEach(x => x.Lines).Where(line => line?.Email != SkippedEmail).NotNull()
                    .DependentRules(() => v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage));
                break;
            case Shape.ForEachAsyncRowFilter:
                v.RuleForEach(x => x.Lines).WhereAsync(line => Task.FromResult(line?.Email != SkippedEmail)).NotNull()
                    .DependentRules(() => v.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage));
                break;
        }

        return v;
    }

    private static Order ModelFor(State state) => state switch
    {
        State.DependentsFail => new Order(),
        State.OwnersFail => new Order
        {
            Description = string.Empty,
            Reference = string.Empty,
            Customer = new Customer { Email = string.Empty },
            Lines = [null!],
        },
        _ => new Order
        {
            Customer = new Customer { Name = "Ada" },
            Lines = [new Customer { Name = "Ada" }],
        },
    };

    private static string Show(IEnumerable<string> issues) => "[" + string.Join(" | ", issues) + "]";

    private static IEnumerable<string> Keys(IEnumerable<ValidationIssue> issues) =>
        issues.Select(issue => $"{issue.Path}: {issue.Message} ({issue.Severity})");

    // Every shape, under Draft and under Submit, over a model whose dependents fail, one whose
    // carrying rules fail, and one that passes: FluentValidation's own answer, the adapter's whole
    // profile, the profile's whole selection in one set, and the profile's groups run one call
    // each all agree. The first two agree by construction; the last two are the rule-by-rule path.
    [Theory]
    [MemberData(nameof(AllShapes))]
    public async Task A_set_of_rules_answers_as_FluentValidation_does_wherever_a_dependent_rule_sits(Shape shape)
    {
        await AssertAgreesAsync(shape, ValidationProfile.Draft);
        await AssertAgreesAsync(shape, ValidationProfile.Submit);
    }

    // A profile beyond Draft and Submit: the Submit ruleset alone, without the rules outside every
    // ruleset. The same four answers agree. Under it the untagged rule two levels inside
    // DeclaredOutsideItsRuleSet does not run, where Submit runs it.
    [Theory]
    [InlineData(Shape.InRuleSet)]
    [InlineData(Shape.RuleForEachInRuleSet)]
    [InlineData(Shape.IncludeInRuleSet)]
    [InlineData(Shape.ChildInRuleSetForEach)]
    [InlineData(Shape.DeclaredOutsideItsRuleSet)]
    public async Task A_profile_beyond_Draft_and_Submit_answers_as_FluentValidation_does(Shape shape)
    {
        var submitOnly = ValidationProfile.Named("SubmitOnly", includeDefaultRules: false, ValidationProfile.SubmitRuleSetName);
        await AssertAgreesAsync(shape, submitOnly);

        if (shape == Shape.DeclaredOutsideItsRuleSet)
        {
            var adapter = new FluentValidationModelValidator<Order>(Build(shape));
            var model = ModelFor(State.DependentsFail);
            var underSubmitOnly = await adapter.ValidateRulesAsync(model, submitOnly, adapter.SelectRules(submitOnly));
            var underSubmit = await adapter.ValidateRulesAsync(model, ValidationProfile.Submit, adapter.SelectRules(ValidationProfile.Submit));
            Assert.DoesNotContain(underSubmitOnly.Report.Issues, issue => issue.Path == "Customer.Name");
            Assert.Contains(underSubmit.Report.Issues, issue => issue.Path == "Customer.Name");
        }
    }

    /// <summary>Under <paramref name="profile"/>, over each model state: FluentValidation's own answer, the adapter's whole profile, the profile's selection in one set and its groups run one call each all agree.</summary>
    private async Task AssertAgreesAsync(Shape shape, ValidationProfile profile)
    {
        foreach (var state in Enum.GetValues<State>())
        {
            var fluent = Build(shape);
            var adapter = new FluentValidationModelValidator<Order>(fluent);
            Assert.True(adapter.CanValidateByRule);

            var model = ModelFor(state);
            var own = await fluent.ValidateAsync(model, options => options.IncludeRuleSets(profile.ToRuleSetNames()));
            var ownKeys = own.Errors.Select(failure =>
                $"{failure.PropertyName}: {failure.ErrorMessage} ({failure.Severity})").ToList();

            var whole = Keys((await adapter.ValidateAsync(model, profile)).Issues).ToList();
            var selection = adapter.SelectRules(profile);
            var oneSet = await adapter.ValidateRulesAsync(model, profile, selection);
            var grouped = new List<string>();
            var scoped = new List<bool>();
            foreach (var group in adapter.GroupBySelectionClass(selection))
            {
                var result = await adapter.ValidateRulesAsync(model, profile, group);
                grouped.AddRange(Keys(result.Report.Issues));
                scoped.Add(result.IsProfileScoped);
            }

            output.WriteLine(
                $"{shape,-27} {profile.Name,-10} {state,-14} FV={Show(ownKeys)} rule-by-rule={Show(Keys(oneSet.Report.Issues))} " +
                $"groups={selection.Count}->{scoped.Count} scoped=[{string.Join(",", scoped)}]");

            Assert.Equal(ownKeys, whole);
            Assert.Equal(whole, Keys(oneSet.Report.Issues));
            Assert.Equal(whole.Order(StringComparer.Ordinal), grouped.Order(StringComparer.Ordinal));
        }
    }

    public static TheoryData<Shape> AllShapes()
    {
        var data = new TheoryData<Shape>();
        foreach (var shape in Enum.GetValues<Shape>())
        {
            data.Add(shape);
        }

        return data;
    }

    // A dependent whose ruleset membership differs from its carrying rule's is admitted by some
    // profiles that select the carrying rule and refused by others, so a set holding that rule
    // answers for the profile it ran under alone. One inside the carrying rule's own ruleset
    // answers every profile that selects it. A dependent declared with no ruleset of its own
    // after the block has closed takes its carrying rule's, but one level further down it takes
    // none (both printed for DeclaredOutsideItsRuleSet), so that set is scoped. A child validator
    // attached inside the dependents counts with its rules: one in a ruleset under an untagged
    // carrying rule scopes the set. An untagged Include() or rule on each row inside them does not.
    // Mutation (2) above makes the four rows of a dependent in another ruleset read false; the
    // child validator's row is a child-context decision, which keeps its scope under (2).
    [Theory]
    [InlineData(Shape.TopLevel, false)]
    [InlineData(Shape.Nested, false)]
    [InlineData(Shape.InRuleSet, false)]
    [InlineData(Shape.TaggedUnderUntagged, true)]
    [InlineData(Shape.UnselectedRuleSetDependent, true)]
    [InlineData(Shape.DeclaredOutsideItsRuleSet, true)]
    [InlineData(Shape.SiblingsOfTaggedUnderUntagged, true)]
    [InlineData(Shape.ChildInsideDependents, true)]
    [InlineData(Shape.IncludeInsideDependents, false)]
    [InlineData(Shape.ForEachInsideDependents, false)]
    public async Task A_dependent_in_another_ruleset_scopes_its_set_to_the_profile(Shape shape, bool scoped)
    {
        var fluent = Build(shape);
        foreach (var rule in (IEnumerable<IValidationRule>)fluent)
        {
            output.WriteLine($"{shape}: {rule.PropertyName} [{string.Join(",", rule.RuleSets ?? [])}] dependents: " +
                string.Join("; ", Flatten(rule).Select(d => $"{d.PropertyName} [{string.Join(",", d.RuleSets ?? [])}]")));
        }

        var adapter = new FluentValidationModelValidator<Order>(fluent);
        var selection = adapter.SelectRules(ValidationProfile.Submit);
        var result = await adapter.ValidateRulesAsync(new Order(), ValidationProfile.Submit, selection);
        Assert.Equal(scoped, result.IsProfileScoped);

        // Each carrying rule takes a group of its own, so its scope marks no sibling: a group
        // holding a rule that carries no dependent rule answers every profile. Mutation (6)
        // above puts SiblingsOfTaggedUnderUntagged's three untagged rules in one scoped group.
        foreach (var group in adapter.GroupBySelectionClass(selection))
        {
            var groupResult = await adapter.ValidateRulesAsync(new Order(), ValidationProfile.Submit, group);
            var holdsSibling = group.Any(identity => identity.Key is IValidationRule rule && rule.DependentRules?.Any() != true);
            output.WriteLine($"group of {group.Count}: holds a sibling={holdsSibling} scoped={groupResult.IsProfileScoped}");
            if (holdsSibling)
            {
                Assert.False(groupResult.IsProfileScoped);
            }
        }

        static IEnumerable<IValidationRule> Flatten(IValidationRule owner) =>
            owner.DependentRules?.SelectMany(d => new[] { d }.Concat(Flatten(d))) ?? [];
    }

    // FluentValidation asks the selector about each dependent rule outside a child context, from
    // inside the rule carrying it, after that rule's own components: the trace shows the order.
    [Fact]
    public void FluentValidation_asks_about_a_dependent_outside_a_child_context()
    {
        var v = new InlineValidator<Order>();
        v.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
            v.RuleFor(x => x.Reference).NotEmpty().DependentRules(() =>
                v.RuleFor(x => x.Customer.Name).NotEmpty()));
        v.RuleFor(x => x.Customer).SetValidator(new DependentCustomerValidator());
        var selector = new Recording();
        v.Validate(new ValidationContext<Order>(new Order(), new PropertyChain(), selector));
        output.WriteLine("calls: " + string.Join(" ; ", selector.Calls));
        Assert.Equal(
            ["Description child=False", "Reference child=False", "Customer.Name child=False", "Customer child=False", "Email child=True", "Name child=True"],
            selector.Calls);
    }

    private sealed class Recording : IValidatorSelector
    {
        public List<string> Calls { get; } = [];

        public bool CanExecute(IValidationRule rule, string propertyPath, IValidationContext context)
        {
            Calls.Add($"{rule.PropertyName} child={context.IsChildContext}");
            return true;
        }
    }

    // The reading files a dependent rule under its own field, under the selector its carrying rule
    // is read by. Its presence demand is required only when every failure of the rules it hangs
    // from blocks a submit, since FluentValidation skips dependents after any failure: a warning
    // on the carrying rule, a condition around it, a condition inside the dependents, or a child
    // validator on the carrying rule leaves the field conditionally required. So does a rule on
    // each row whose every check carries a condition, since FluentValidation skips the whole rule,
    // dependents included, while no check's condition holds; one unconditional check keeps the
    // field required. Mutation (3) above drops Customer.Name from every declared row. Mutation (5)
    // reads the ForEachEveryCheckConditional row as Required.
    [Theory]
    [InlineData(Shape.TopLevel, "Submit", FieldRequirement.Required)]
    [InlineData(Shape.TopLevel, "Draft", FieldRequirement.Required)]
    [InlineData(Shape.Nested, "Submit", FieldRequirement.Required)]
    [InlineData(Shape.OnRuleForEach, "Submit", FieldRequirement.Required)]
    [InlineData(Shape.WhenAround, "Submit", FieldRequirement.ConditionallyRequired)]
    [InlineData(Shape.OtherwiseAround, "Submit", FieldRequirement.ConditionallyRequired)]
    [InlineData(Shape.WhenInsideDependents, "Submit", FieldRequirement.ConditionallyRequired)]
    [InlineData(Shape.OnSetValidatorRule, "Submit", FieldRequirement.ConditionallyRequired)]
    [InlineData(Shape.WarningOwner, "Submit", FieldRequirement.ConditionallyRequired)]
    [InlineData(Shape.InRuleSet, "Submit", FieldRequirement.Required)]
    [InlineData(Shape.InRuleSet, "Draft", null)]
    [InlineData(Shape.TaggedUnderUntagged, "Submit", FieldRequirement.Required)]
    [InlineData(Shape.TaggedUnderUntagged, "Draft", null)]
    [InlineData(Shape.UnselectedRuleSetDependent, "Submit", null)]
    [InlineData(Shape.DeclaredOutsideItsRuleSet, "Submit", FieldRequirement.Required)]
    [InlineData(Shape.DeclaredOutsideItsRuleSet, "Draft", null)]
    [InlineData(Shape.ForEachEveryCheckConditional, "Submit", FieldRequirement.ConditionallyRequired)]
    [InlineData(Shape.ForEachOneCheckUnconditional, "Submit", FieldRequirement.Required)]
    [InlineData(Shape.ForEachRowFilter, "Submit", FieldRequirement.Required)]
    [InlineData(Shape.ForEachAsyncRowFilter, "Submit", FieldRequirement.Required)]
    public void The_reading_declares_a_dependent_rules_field(Shape shape, string profileName, FieldRequirement? expected)
    {
        var profile = profileName == "Submit" ? ValidationProfile.Submit : ValidationProfile.Draft;
        var adapter = new FluentValidationModelValidator<Order>(Build(shape));
        var declared = adapter.GetDeclaredFieldPaths(profile);
        output.WriteLine($"{shape} {profileName}: declared [{string.Join(", ", declared.Order(StringComparer.Ordinal))}]" +
            $" Customer.Name={adapter.GetFieldRequirement("Customer.Name", profile)}");

        if (expected is { } requirement)
        {
            Assert.Contains("Customer.Name", declared);
            Assert.Equal(requirement, adapter.GetFieldRequirement("Customer.Name", profile));
        }
        else
        {
            Assert.DoesNotContain("Customer.Name", declared);
            Assert.Equal(FieldRequirement.NotRequired, adapter.GetFieldRequirement("Customer.Name", profile));
        }

        // The middle link of the chain is a dependent rule's field too.
        if (shape == Shape.Nested)
        {
            Assert.Equal(FieldRequirement.Required, adapter.GetFieldRequirement("Reference", profile));
        }
    }

    // What the Required rows for a row filter rest on, measured: with every row filtered out, with
    // no rows, and with the rows passing, FluentValidation runs the dependent rule and reports the
    // empty name, so no model that passes the carrying rule skips it. The filter still makes the
    // rule's own per-row check conditional, since which rows it judges depends on the model.
    // Mutation (7) above reads Customer.Name as ConditionallyRequired.
    [Theory]
    [InlineData(Shape.ForEachRowFilter)]
    [InlineData(Shape.ForEachAsyncRowFilter)]
    public async Task A_row_filter_skips_rows_and_never_the_dependent_rules(Shape shape)
    {
        var fluent = Build(shape);
        Order[] models =
        [
            new() { Lines = [new Customer { Email = SkippedEmail }] },
            new() { Lines = [] },
            new() { Lines = [new Customer()] },
        ];

        foreach (var model in models)
        {
            model.Customer.Name = null;
            var own = await fluent.ValidateAsync(model, options => options.IncludeRuleSets(ValidationProfile.Submit.ToRuleSetNames()));
            Assert.Equal([DependentMessage], own.Errors.Select(failure => failure.ErrorMessage));
        }

        var adapter = new FluentValidationModelValidator<Order>(fluent);
        Assert.Equal(FieldRequirement.Required, adapter.GetFieldRequirement("Customer.Name", ValidationProfile.Submit));
        Assert.Equal(FieldRequirement.ConditionallyRequired, adapter.GetFieldRequirement("Lines[]", ValidationProfile.Submit));
    }

    // A dependent rule inside a child validator, and a child validator, an Include() or a rule on
    // each row inside DependentRules, files under the path its failures carry, read under the
    // selection a run gives it: the child's Submit rule is absent under Draft, and the Submit
    // ruleset's rule on each row is absent under Draft.
    [Theory]
    [InlineData(Shape.InChild, "Submit", "Customer.Name", FieldRequirement.Required)]
    [InlineData(Shape.InChild, "Draft", "Customer.Name", FieldRequirement.Required)]
    [InlineData(Shape.ChildInRuleSetForEach, "Submit", "Lines[].Name", FieldRequirement.Required)]
    [InlineData(Shape.ChildInRuleSetForEach, "Draft", "Lines[].Name", null)]
    [InlineData(Shape.ChildInsideDependents, "Submit", "Customer.Name", FieldRequirement.Required)]
    [InlineData(Shape.ChildInsideDependents, "Draft", "Customer.Name", null)]
    [InlineData(Shape.ChildInsideDependents, "Draft", "Customer.Email", FieldRequirement.Required)]
    [InlineData(Shape.IncludeInsideDependents, "Submit", "Customer.Name", FieldRequirement.Required)]
    [InlineData(Shape.ForEachInsideDependents, "Submit", "Lines[]", FieldRequirement.Required)]
    public void The_reading_declares_a_field_a_dependent_rule_reaches(
        Shape shape, string profileName, string path, FieldRequirement? expected)
    {
        var profile = profileName == "Submit" ? ValidationProfile.Submit : ValidationProfile.Draft;
        var adapter = new FluentValidationModelValidator<Order>(Build(shape));
        var declared = adapter.GetDeclaredFieldPaths(profile);
        output.WriteLine($"{shape} {profileName}: declared [{string.Join(", ", declared.Order(StringComparer.Ordinal))}]" +
            $" {path}={adapter.GetFieldRequirement(path, profile)}");

        if (expected is { } requirement)
        {
            Assert.Contains(path, declared);
            Assert.Equal(requirement, adapter.GetFieldRequirement(path, profile));
        }
        else
        {
            Assert.DoesNotContain(path, declared);
            Assert.Equal(FieldRequirement.NotRequired, adapter.GetFieldRequirement(path, profile));
        }
    }
}
