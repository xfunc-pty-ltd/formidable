using System.Collections.Concurrent;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Results;

namespace Formidable.Tests;

/// <summary>
/// The check a profile that leaves out the default rules meets on a plain validator: when it
/// selects no rule, validating under it throws <see cref="InvalidOperationException"/>. One test
/// replaces FluentValidation's global selector factory, and the rest count rules through it, hence
/// the collection. A profile seen to select a rule is remembered per validator type for the life of
/// the process, and one that selects nothing is never remembered, so a test that depends on what is
/// remembered validates under a profile of its own (a name and a ruleset no other test uses).
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public class EmptyProfileSelectionCheckTests
{
    /// <summary>A profile that leaves out the default rules and names one ruleset no validator here declares.</summary>
    private static ValidationProfile PhantomProfile(string name) =>
        ValidationProfile.Named(name, includeDefaultRules: false, name + "Set");

    // Mutation: drop the check from one entry point (the Validate or ValidateAsync extension, which
    // the adapter's own two validations call, or the adapter's ruleset check that SelectRules,
    // ValidateRulesAsync and the inspection members share), so that member's row validates under
    // the profile and accepts every value. Mutation: remember the empty answer and return early on
    // the next call, so the second call through each member passes.
    [Theory]
    [InlineData("ExtensionValidate")]
    [InlineData("ExtensionValidateAsync")]
    [InlineData("Validate")]
    [InlineData("ValidateAsync")]
    [InlineData("SelectRules")]
    [InlineData("ValidateRulesAsync")]
    [InlineData("GetFieldRequirement")]
    [InlineData("GetDeclaredFieldPaths")]
    public async Task A_profile_selecting_no_rule_of_a_plain_validator_throws_at_every_entry_point(string member)
    {
        var validator = new PlainPhantomProfileModelValidator();
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(validator);
        var model = new PhantomProfileModel();
        var phantom = PhantomProfile("Via" + member);

        async Task Call()
        {
            switch (member)
            {
                case "ExtensionValidate":
                    validator.Validate(model, phantom);
                    break;
                case "ExtensionValidateAsync":
                    await validator.ValidateAsync(model, phantom);
                    break;
                case "Validate":
                    adapter.Validate(model, phantom);
                    break;
                case "ValidateAsync":
                    await adapter.ValidateAsync(model, phantom);
                    break;
                case "SelectRules":
                    adapter.SelectRules(phantom);
                    break;
                case "ValidateRulesAsync":
                    await adapter.ValidateRulesAsync(model, phantom, []);
                    break;
                case "GetFieldRequirement":
                    adapter.GetFieldRequirement(nameof(PhantomProfileModel.Name), phantom);
                    break;
                case "GetDeclaredFieldPaths":
                    adapter.GetDeclaredFieldPaths(phantom);
                    break;
            }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(Call);
        await Assert.ThrowsAsync<InvalidOperationException>(Call);
    }

    // The top-level rules carry 'Review' twice and 'Approve' once, listed once each in ordinal
    // order. The included validator's rule carries 'Nested', which no top-level rule carries, so it
    // is not listed.
    // Mutation: list the rulesets of the included validator's rules too, or list them in
    // declaration order, or drop the duplicates' merge, so the first message differs. Mutation:
    // print an empty list as nothing at all, so the second message differs.
    [Fact]
    public void The_message_names_the_profile_the_validator_and_its_top_level_rulesets()
    {
        var typo = ValidationProfile.Named("Escalte", includeDefaultRules: false, "Escalte", "Aprove");

        var carried = Assert.Throws<InvalidOperationException>(
            () => new CarriedRuleSetsPhantomProfileModelValidator().Validate(new PhantomProfileModel(), typo));
        var none = Assert.Throws<InvalidOperationException>(
            () => new DefaultOnlyPhantomProfileModelValidator().Validate(new PhantomProfileModel(), typo));

        Assert.Equal(
            "Profile 'Escalte' leaves out the default rules, and no rule of " +
            "'CarriedRuleSetsPhantomProfileModelValidator' carries its rulesets ('Escalte', 'Aprove'), " +
            "so validating under it would check nothing. Rulesets its top-level rules carry: 'Approve', 'Review'.",
            carried.Message);
        Assert.Equal(
            "Profile 'Escalte' leaves out the default rules, and no rule of " +
            "'DefaultOnlyPhantomProfileModelValidator' carries its rulesets ('Escalte', 'Aprove'), " +
            "so validating under it would check nothing. Rulesets its top-level rules carry: none.",
            none.Message);
    }

    // The profile is never remembered as empty, so the equal profile built afresh is counted and
    // refused too.
    // Mutation: remember the empty answer, so every call after the first passes.
    [Fact]
    public async Task A_profile_selecting_no_rule_throws_on_every_call()
    {
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator());
        var model = new PhantomProfileModel();
        var phantom = PhantomProfile("EveryCall");

        // Equal to phantom by full shape (names compare case-insensitively), but its own instance.
        var equal = ValidationProfile.Named("everycall", includeDefaultRules: false, "everycallset");

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ValidateAsync(model, phantom));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ValidateAsync(model, phantom));
        Assert.Throws<InvalidOperationException>(() => adapter.SelectRules(equal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ValidateRulesAsync(model, equal, []));
    }

    // A profile the type selects is remembered as selecting, and a second profile that selects
    // nothing is judged on its own.
    // Mutation: key the record by validator type alone, so the second profile passes.
    [Fact]
    public async Task A_second_profile_selecting_no_rule_throws_on_its_own()
    {
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator());
        var model = new PhantomProfileModel();

        await adapter.ValidateAsync(
            model, ValidationProfile.Named("FirstOfTwo", includeDefaultRules: false, ValidationProfile.SubmitRuleSetName));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ValidateAsync(model, PhantomProfile("SecondOfTwo")));

        Assert.Contains("'SecondOfTwo'", exception.Message);
        Assert.Contains("'SecondOfTwoSet'", exception.Message);
    }

    // The minimal-API shape: the server resolves a new adapter per request over the one validator
    // the container holds.
    // Mutation: remember the empty answer on the validator, so the second adapter passes.
    [Fact]
    public async Task Two_adapters_over_one_validator_each_throw()
    {
        var validator = new PlainPhantomProfileModelValidator();
        var model = new PhantomProfileModel();
        var profile = PhantomProfile("TwoAdapters");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FluentValidationModelValidator<PhantomProfileModel>(validator).ValidateAsync(model, profile));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FluentValidationModelValidator<PhantomProfileModel>(validator).ValidateAsync(model, profile));
    }

    // A scoped validator: every request builds a new instance of the one type, inside a new adapter.
    // Mutation: remember the empty answer by validator type, so the second instance passes.
    [Fact]
    public async Task Two_validators_of_one_type_each_throw()
    {
        var model = new PhantomProfileModel();
        var profile = PhantomProfile("TwoInstances");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator())
                .ValidateAsync(model, profile));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator())
                .ValidateAsync(model, profile));
    }

    // Each validator type is judged on its own rules, so a profile one type selects still throws for
    // a type it selects nothing of, naming that type.
    // Mutation: key the record by profile alone, so the second type passes.
    [Fact]
    public async Task Two_validator_types_under_one_profile_are_each_judged()
    {
        var model = new PhantomProfileModel();
        var profile = PhantomProfile("TwoTypes");

        await new FluentValidationModelValidator<PhantomProfileModel>(new RuleSetPartValidator("TwoTypesSet"))
            .ValidateAsync(model, profile);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator())
                .ValidateAsync(model, profile));

        Assert.Contains($"'{nameof(PlainPhantomProfileModelValidator)}'", exception.Message);
    }

    // The validator type's constructor argument decides whether it carries the ruleset, so two
    // instances of one type can answer differently. The first instance seen to select spares every
    // later one; an instance that selects nothing spares none.
    // Mutation: key the record by validator instance, so the empty instance after a selecting one
    // throws. Mutation: remember the empty answer as refusing, so the selecting instance after an
    // empty one throws.
    [Fact]
    public void A_selecting_answer_is_shared_by_the_validator_type()
    {
        var model = new PhantomProfileModel();
        var selectingFirst = ValidationProfile.Named("SelectingFirst", includeDefaultRules: false, "Flagged");
        var emptyFirst = ValidationProfile.Named("EmptyFirst", includeDefaultRules: false, "Flagged");

        new FlaggedPhantomProfileModelValidator(withRuleSet: true).Validate(model, selectingFirst);
        new FlaggedPhantomProfileModelValidator(withRuleSet: false).Validate(model, selectingFirst);

        Assert.Throws<InvalidOperationException>(
            () => new FlaggedPhantomProfileModelValidator(withRuleSet: false).Validate(model, emptyFirst));
        new FlaggedPhantomProfileModelValidator(withRuleSet: true).Validate(model, emptyFirst);
        new FlaggedPhantomProfileModelValidator(withRuleSet: false).Validate(model, emptyFirst);
    }

    // Mutation: invert the zero test, so a profile that selects rules throws.
    [Fact]
    public async Task A_profile_that_selects_rules_does_not_throw()
    {
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator());
        var submitOnly = ValidationProfile.Named("SubmitOnly", includeDefaultRules: false, ValidationProfile.SubmitRuleSetName);

        // One profile shared by several validators can name a ruleset only some of them carry.
        var shared = ValidationProfile.Named("Shared", includeDefaultRules: false, ValidationProfile.SubmitRuleSetName, "NotOnThisOne");

        await adapter.ValidateAsync(new PhantomProfileModel(), submitOnly);
        Assert.NotEmpty(adapter.SelectRules(submitOnly));
        await adapter.ValidateAsync(new PhantomProfileModel(), shared);
    }

    // Every profile here includes the default rules and selects nothing of its validator, so only
    // the IncludeDefaultRules test keeps it from throwing. The built-in Submit names "Submit", which
    // the validator here never declares; Draft selects nothing of a validator whose only rules sit
    // in a ruleset; FromName, the [Validate] route, always includes the default rules.
    // Mutation: drop the IncludeDefaultRules test, so each of these throws.
    [Fact]
    public async Task A_profile_that_includes_the_default_rules_never_throws()
    {
        var submitOnly = new FluentValidationModelValidator<PhantomProfileModel>(new SubmitOnlyPhantomProfileModelValidator());
        var withDefaults = ValidationProfile.Named("P", includeDefaultRules: true, "NoSuchSet");

        Assert.Empty(submitOnly.SelectRules(withDefaults));
        await submitOnly.ValidateAsync(new PhantomProfileModel(), withDefaults);
        await submitOnly.ValidateAsync(new PhantomProfileModel(), ValidationProfile.Draft);
        await submitOnly.ValidateAsync(new PhantomProfileModel(), ValidationProfile.FromName("Escalate"));
        await new FluentValidationModelValidator<PhantomProfileModel>(new RuleSetPartValidator("A"))
            .ValidateAsync(new PhantomProfileModel(), ValidationProfile.Submit);
    }

    // Each round races several threads through the first calls under two profiles of the round's
    // own: one the validator type selects and one it selects nothing of. Lined up on a barrier,
    // every thread holds its own adapter over its own instance of one validator type, as a server
    // with a scoped validator does. Every call under the empty profile must throw, and every call
    // under the selecting one must pass.
    // Mutation: replace the concurrent record with a plain Dictionary, which racing writers can
    // corrupt. Whether a run catches it is a race, so one green run under the mutation proves
    // nothing.
    [Fact]
    public void Concurrent_first_calls_under_a_profile_selecting_no_rule_each_throw()
    {
        const int rounds = 300;
        const int threads = 8;
        var failures = new ConcurrentQueue<string>();
        var refused = 0;

        for (var round = 0; round < rounds; round++)
        {
            var model = new PhantomProfileModel();
            var empty = PhantomProfile("Concurrent" + round);
            var selecting = ValidationProfile.Named("ConcurrentSelecting" + round, includeDefaultRules: false, ValidationProfile.SubmitRuleSetName);
            using var barrier = new Barrier(threads);

            var workers = Enumerable.Range(0, threads)
                .Select(index => (index, adapter: new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator())))
                .Select(worker => new Thread(() =>
                {
                    var profile = worker.index % 2 == 0 ? empty : selecting;
                    try
                    {
                        barrier.SignalAndWait();
                        worker.adapter.Validate(model, profile);
                        if (profile == empty)
                        {
                            failures.Enqueue($"round {round}: a call under the empty profile passed");
                        }
                    }
                    catch (InvalidOperationException exception) when (profile == empty && exception.Message.StartsWith($"Profile '{empty.Name}'", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref refused);
                    }
                    catch (Exception exception)
                    {
                        failures.Enqueue($"round {round}: {exception.GetType().Name}: {exception.Message}");
                    }
                }))
                .ToList();

            workers.ForEach(worker => worker.Start());
            workers.ForEach(worker => worker.Join());
        }

        Assert.Empty(failures);
        Assert.Equal(rounds * threads / 2, refused);
    }

    // Mutation: drop the ProfiledValidator test, so the validator is judged as a plain one and
    // throws the empty-selection message instead of naming the unregistered ruleset.
    [Fact]
    public async Task A_ProfiledValidator_throws_its_own_message()
    {
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new ProfiledPhantomProfileModelValidator());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ValidateAsync(new PhantomProfileModel(), PhantomProfile("ProfiledThrows")));

        Assert.StartsWith("Profile ruleset 'ProfiledThrowsSet' matches no ruleset", exception.Message);
    }

    // A ProfiledValidator that registers a ruleset with no rule in it: the profile passes the
    // ruleset check and selects nothing, and the ProfiledValidator test alone keeps it from throwing.
    // Mutation: drop the ProfiledValidator test, so this profile throws.
    [Fact]
    public async Task A_ProfiledValidator_with_an_empty_ruleset_does_not_throw()
    {
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new ProfiledPhantomProfileModelValidator());
        var emptyOnly = ValidationProfile.Named("EmptyOnly", includeDefaultRules: false, "Empty");

        Assert.Empty(adapter.SelectRules(emptyOnly));
        await adapter.ValidateAsync(new PhantomProfileModel(), emptyOnly);
    }

    // A replaced selector factory whose selector reads the model cannot answer the model-less
    // count. The count gives up and counts the selection as selecting, and the call validates and
    // returns its report.
    // The factory is process-wide and classes outside the collection walk model-less selections
    // meanwhile, so the installed selector refuses only this class's model and hands every other
    // question to the stock selector.
    // Mutation: drop the try/catch around the count, so the selector's own exception escapes.
    // Mutation: count a selection that cannot be counted as selecting nothing, so the check throws.
    [Fact]
    public async Task An_uncountable_selection_counts_as_selecting()
    {
        var stock = ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory;
        try
        {
            ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory =
                ruleSets => new ModelReadingSelector(stock(ruleSets));
            var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator());

            var report = await adapter.ValidateAsync(new PhantomProfileModel(), PhantomProfile("ModelReadingSelector"));

            Assert.True(report.IsValid);
        }
        finally
        {
            ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory = stock;
        }
    }

    // A validator that cannot list its rules is not an AbstractValidator, so the check never meets it.
    // Mutation: judge every validator under such a profile, counting one that cannot list its rules
    // as selecting none, so it throws.
    [Fact]
    public async Task A_hand_rolled_validator_is_never_checked()
    {
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new HandRolledPhantomProfileModelValidator());
        var model = new PhantomProfileModel();
        var profile = PhantomProfile("HandRolled");

        await adapter.ValidateAsync(model, profile);
        adapter.Validate(model, profile);
        adapter.GetDeclaredFieldPaths(profile);
    }

    // A validator that declares no rule of its own but forwards one of its entry points to another
    // validator is validated by whatever that validator selects, so its own rules cannot say the
    // profile checks nothing. Each row overrides one entry point and validates through it: the
    // inner validator's failure comes back, as FluentValidation itself answers. The control, with
    // no override and no rule, is still refused. Mutation: drop either override from the count,
    // and that row throws the refusal.
    [Theory]
    [InlineData("Validate")]
    [InlineData("ValidateAsync")]
    public async Task A_validator_forwarding_through_an_entry_point_counts_as_selecting(string entryPoint)
    {
        var profile = ValidationProfile.Named("Forwarded" + entryPoint, includeDefaultRules: false, ForwardedRuleSet);
        var model = new PhantomProfileModel();
        IEnumerable<ValidationFailure> failures;
        if (entryPoint == "Validate")
        {
            var validator = new SyncForwardingPhantomProfileModelValidator(new ForwardedPhantomProfileModelValidator());
            failures = validator.Validate(model, profile).Errors;
        }
        else
        {
            var validator = new AsyncForwardingPhantomProfileModelValidator(new ForwardedPhantomProfileModelValidator());
            failures = (await validator.ValidateAsync(model, profile)).Errors;
            var report = await new FluentValidationModelValidator<PhantomProfileModel>(validator).ValidateAsync(model, profile);
            Assert.Equal([ForwardedMessage], report.Issues.Select(issue => issue.Message));
        }

        Assert.Equal([ForwardedMessage], failures.Select(failure => failure.ErrorMessage));
        await RefusedOnce(new EmptyPhantomProfileModelValidator(), ValidationProfile.Named("Unforwarded" + entryPoint, includeDefaultRules: false, ForwardedRuleSet));
    }

    /// <summary>A profile that leaves out the default rules and selects ruleset <c>B</c>, which no included validator here declares.</summary>
    private static readonly ValidationProfile OnlyB = ValidationProfile.Named("B", includeDefaultRules: false, "B");

    /// <summary>A profile that leaves out the default rules and selects ruleset <c>A</c>, the one every included validator here declares.</summary>
    private static readonly ValidationProfile OnlyA = ValidationProfile.Named("A", includeDefaultRules: false, "A");

    // Validates under the profile through an adapter, the way a form or a server filter does.
    private static Task<ValidationReport> ValidateOnce(IValidator<PhantomProfileModel> validator, ValidationProfile profile) =>
        new FluentValidationModelValidator<PhantomProfileModel>(validator).ValidateAsync(new PhantomProfileModel(), profile);

    // Validates once under the profile and returns what it threw, which must be the check's refusal.
    private static Task<InvalidOperationException> RefusedOnce(IValidator<PhantomProfileModel> validator, ValidationProfile profile) =>
        Assert.ThrowsAsync<InvalidOperationException>(() => ValidateOnce(validator, profile));

    // FluentValidation's selector admits an Include outside any ruleset under every named ruleset,
    // so the check has to look at the included validator's own rules, all in ruleset A here.
    // Mutation: count an admitted Include as selecting a rule. Nothing then throws.
    [Fact]
    public async Task An_include_whose_rules_the_profile_skips_throws()
    {
        var exception = await RefusedOnce(new InstanceIncludeSkippedPhantomProfileModelValidator(), OnlyB);

        Assert.Contains(nameof(InstanceIncludeSkippedPhantomProfileModelValidator), exception.Message);
    }

    // A pin: the included validator's rule in ruleset A is a rule the profile selects.
    // Mutation: never look inside an Include, and count it as selecting nothing. The call then
    // throws.
    [Fact]
    public async Task An_include_whose_rules_the_profile_selects_does_not_throw()
    {
        await ValidateOnce(new InstanceIncludeSelectedPhantomProfileModelValidator(), OnlyA);
    }

    // A pin: an Include that picks its validator by reading the model is built by a factory, so it
    // counts as selecting a rule without being read, and nothing throws.
    // Mutation: count an Include built by a factory as selecting nothing. The call then throws.
    [Fact]
    public async Task An_include_built_from_the_model_counts_as_selecting()
    {
        await ValidateOnce(new ModelBuiltIncludePhantomProfileModelValidator(), OnlyB);
    }

    // The check looks at what an Include holds, never at what its factory builds, so a factory
    // that ignores its argument counts as selecting like any other.
    // Mutation: drop the factory test, so the factory's ruleset-A validator is read and selects
    // nothing under B. The call then throws.
    [Fact]
    public async Task A_factory_include_that_ignores_its_argument_counts_as_selecting()
    {
        await ValidateOnce(new FactoryIncludePhantomProfileModelValidator(), OnlyB);
    }

    // The factory copes with a missing model and builds the ruleset-A validator then, while a model
    // with Flag set gets the ruleset-B one, which the profile runs: the model decides, so the
    // Include counts as selecting.
    // Mutation: drop the factory test, so the validator a missing model picks is read and the call
    // throws.
    [Fact]
    public async Task A_factory_include_that_copes_with_no_model_counts_as_selecting()
    {
        await ValidateOnce(new NullTolerantIncludePhantomProfileModelValidator(), OnlyB);

        var flagged = await new FluentValidationModelValidator<PhantomProfileModel>(new NullTolerantIncludePhantomProfileModelValidator())
            .ValidateAsync(new PhantomProfileModel { Flag = true }, OnlyB);
        Assert.Single(flagged.Issues);
    }

    // The same choice over two validators the validator holds: the factory hands back the same
    // instance on every read with no model, so only what the Include holds can tell it from an
    // Include of an instance.
    // Mutation: drop the factory test, so the held ruleset-A validator a missing model picks is read
    // and the call throws. Reading the Include twice and comparing the instances answers the same.
    [Fact]
    public async Task A_factory_include_that_hands_back_held_validators_counts_as_selecting()
    {
        await ValidateOnce(new HeldChoiceIncludePhantomProfileModelValidator(), OnlyB);

        var flagged = await new FluentValidationModelValidator<PhantomProfileModel>(new HeldChoiceIncludePhantomProfileModelValidator())
            .ValidateAsync(new PhantomProfileModel { Flag = true }, OnlyB);
        Assert.Single(flagged.Issues);
    }

    // The Include sits under a condition that never holds, so validating it ends; reading it
    // ignores conditions and meets the validator again.
    // Mutation: drop the guard against a validator met twice. The read then recurses until the
    // stack overflows, which takes the test host down, so run that mutation alone with a filter.
    [Fact]
    public async Task A_validator_that_includes_itself_is_judged_once()
    {
        var exception = await RefusedOnce(new SelfIncludingPhantomProfileModelValidator(), OnlyB);

        Assert.Contains(nameof(SelfIncludingPhantomProfileModelValidator), exception.Message);
    }

    // A factory building the validator's own type would hand back a new instance on every read, but
    // an Include built by a factory counts as selecting before anything is read, so the read never
    // goes round.
    // Mutation: drop the factory test. The read guard then stops at the built instance of a type on
    // the way down, the ruleset-A rule selects nothing under B, and the call throws.
    [Fact]
    public async Task A_validator_whose_include_builds_its_own_type_counts_as_selecting()
    {
        await ValidateOnce(new SelfFactoryPhantomProfileModelValidator(), OnlyB);
    }

    // An Include two levels down is judged by the innermost validator's rules, all in ruleset A.
    // Mutation: look inside one Include only, and count an Include found there as selecting a
    // rule. Nothing then throws.
    [Fact]
    public async Task A_nested_include_whose_rules_the_profile_skips_throws()
    {
        var exception = await RefusedOnce(new NestedIncludePhantomProfileModelValidator(), OnlyB);

        Assert.Contains(nameof(NestedIncludePhantomProfileModelValidator), exception.Message);
    }

    // Two includes of one type with other constructor arguments: the first holds a ruleset-A rule,
    // the second a ruleset-B rule, which the profile selects. A type seen once does not stop the
    // read of a sibling.
    // Mutation: keep one set of the types looked into for the whole read, and skip any type in it.
    // The second include is then skipped and the call throws.
    [Fact]
    public async Task Two_included_validators_of_one_type_are_each_judged()
    {
        await ValidateOnce(new HeldSiblingIncludesPhantomProfileModelValidator(), OnlyB);
    }

    // The same two includes, each built by a factory: each counts as selecting without being read.
    // Mutation: count an Include built by a factory as selecting nothing. The call then throws.
    [Fact]
    public async Task Two_factory_includes_of_one_type_count_as_selecting()
    {
        await ValidateOnce(new FactorySiblingIncludesPhantomProfileModelValidator(), OnlyB);
    }

    // An included validator that includes another instance of its own type, held since
    // construction: the inner one holds the ruleset-B rule. A held validator nests only as deep as
    // its constructors built it, so it is read.
    // Mutation: stop at any validator whose type is already on the way down, held or built. The
    // inner include is then skipped and the call throws.
    [Fact]
    public async Task An_include_nested_in_its_own_type_is_judged_to_its_depth()
    {
        await ValidateOnce(new NestedSameTypeIncludePhantomProfileModelValidator(), OnlyB);
    }

    // A pin: an included validator that cannot list its rules cannot be counted, so it counts as
    // selecting a rule and nothing throws.
    // Mutation: count an included validator that cannot list its rules as selecting nothing. The
    // call then throws.
    [Fact]
    public async Task An_included_validator_that_cannot_list_its_rules_counts_as_selecting()
    {
        await ValidateOnce(new HandRolledIncludePhantomProfileModelValidator(), OnlyB);
    }

    public sealed class PhantomProfileModel
    {
        public string Name { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;

        public bool Flag { get; set; }
    }

    private const string ForwardedRuleSet = "Forwarded";
    private const string ForwardedMessage = "Code is required where it is forwarded";

    /// <summary>The validator the forwarding validators hand their context to: one rule, in the forwarded ruleset.</summary>
    public sealed class ForwardedPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public ForwardedPhantomProfileModelValidator() =>
            RuleSet(ForwardedRuleSet, () => RuleFor(x => x.Code).NotEmpty().WithMessage(ForwardedMessage));
    }

    /// <summary>Declares no rule, and forwards the synchronous entry point to another validator.</summary>
    public sealed class SyncForwardingPhantomProfileModelValidator(IValidator<PhantomProfileModel> inner) : AbstractValidator<PhantomProfileModel>
    {
        public override ValidationResult Validate(ValidationContext<PhantomProfileModel> context) => inner.Validate(context);
    }

    /// <summary>Declares no rule, and forwards the asynchronous entry point to another validator.</summary>
    public sealed class AsyncForwardingPhantomProfileModelValidator(IValidator<PhantomProfileModel> inner) : AbstractValidator<PhantomProfileModel>
    {
        public override Task<ValidationResult> ValidateAsync(ValidationContext<PhantomProfileModel> context, CancellationToken cancellation = default) =>
            inner.ValidateAsync(context, cancellation);
    }

    /// <summary>Declares no rule and overrides nothing: the control the forwarding validators are compared with.</summary>
    public sealed class EmptyPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
    }

    /// <summary>A default rule and a <c>"Submit"</c> rule, with no ruleset named anything else.</summary>
    public sealed class PlainPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public PlainPhantomProfileModelValidator()
        {
            RuleFor(x => x.Name).NotEmpty();
            RuleSet(ValidationProfile.SubmitRuleSetName, () => RuleFor(x => x.Code).NotEmpty());
        }
    }

    /// <summary>No default rule at all: only the <c>"Submit"</c> ruleset.</summary>
    public sealed class SubmitOnlyPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public SubmitOnlyPhantomProfileModelValidator() =>
            RuleSet(ValidationProfile.SubmitRuleSetName, () => RuleFor(x => x.Code).NotEmpty());
    }

    /// <summary>Default rules only, so its top-level rules carry no ruleset.</summary>
    public sealed class DefaultOnlyPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public DefaultOnlyPhantomProfileModelValidator() => RuleFor(x => x.Name).NotEmpty();
    }

    /// <summary>Top-level rules in <c>Review</c> (twice) and <c>Approve</c>, and an included validator whose rule is in <c>Nested</c>.</summary>
    public sealed class CarriedRuleSetsPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public CarriedRuleSetsPhantomProfileModelValidator()
        {
            RuleFor(x => x.Name).NotEmpty();
            RuleSet("Review", () => RuleFor(x => x.Name).MaximumLength(10));
            RuleSet("Approve", () => RuleFor(x => x.Code).NotEmpty());
            RuleSet("Review", () => RuleFor(x => x.Code).MaximumLength(10));
            Include(new RuleSetPartValidator("Nested"));
        }
    }

    public sealed class ProfiledPhantomProfileModelValidator : ProfiledValidator<PhantomProfileModel>
    {
        protected override void ConfigureCommonRules() => RuleFor(x => x.Name).NotEmpty();

        protected override void ConfigureProfiles()
        {
            Profile(ValidationProfile.SubmitRuleSetName, () => RuleFor(x => x.Code).NotEmpty());
            Profile("Empty", () => { });
        }
    }

    /// <summary>One rule in ruleset <c>Flagged</c> when its constructor argument says so, and no rule otherwise.</summary>
    public sealed class FlaggedPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public FlaggedPhantomProfileModelValidator(bool withRuleSet)
        {
            if (withRuleSet)
            {
                RuleSet("Flagged", () => RuleFor(x => x.Name).NotEmpty());
            }
        }
    }

    // The included validators below are never validated on their own under a profile that leaves
    // out the default rules, except RuleSetPartValidator under profiles no other test uses, so
    // they are shared.

    /// <summary>One rule, in ruleset <c>A</c>.</summary>
    public sealed class IncludedPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public IncludedPhantomProfileModelValidator() =>
            RuleSet("A", () => RuleFor(x => x.Name).NotEmpty());
    }

    /// <summary>The same rule as <see cref="IncludedPhantomProfileModelValidator"/>, under a type of its own.</summary>
    public sealed class SecondIncludedPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public SecondIncludedPhantomProfileModelValidator() =>
            RuleSet("A", () => RuleFor(x => x.Code).NotEmpty());
    }

    public sealed class InstanceIncludeSkippedPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public InstanceIncludeSkippedPhantomProfileModelValidator() => Include(new IncludedPhantomProfileModelValidator());
    }

    public sealed class InstanceIncludeSelectedPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public InstanceIncludeSelectedPhantomProfileModelValidator() => Include(new IncludedPhantomProfileModelValidator());
    }

    public sealed class ModelBuiltIncludePhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public ModelBuiltIncludePhantomProfileModelValidator() =>
            Include(x => x.Flag
                ? new IncludedPhantomProfileModelValidator()
                : (IValidator<PhantomProfileModel>)new SecondIncludedPhantomProfileModelValidator());
    }

    /// <summary>Picks its included validator from the model, and picks the ruleset-A one when there is no model.</summary>
    public sealed class NullTolerantIncludePhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public NullTolerantIncludePhantomProfileModelValidator() =>
            Include(x => x?.Flag == true ? new RuleSetPartValidator("B") : new RuleSetPartValidator("A"));
    }

    /// <summary>Picks one of two validators it holds from the model, and the ruleset-A one when there is no model.</summary>
    public sealed class HeldChoiceIncludePhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        private readonly RuleSetPartValidator _flagged = new("B");
        private readonly RuleSetPartValidator _plain = new("A");

        public HeldChoiceIncludePhantomProfileModelValidator() =>
            Include(x => x?.Flag == true ? _flagged : _plain);
    }

    public sealed class FactoryIncludePhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public FactoryIncludePhantomProfileModelValidator() => Include(_ => new IncludedPhantomProfileModelValidator());
    }

    public sealed class SelfIncludingPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public SelfIncludingPhantomProfileModelValidator()
        {
            RuleSet("A", () => RuleFor(x => x.Name).NotEmpty());
            When(_ => false, () => Include(this));
        }
    }

    public sealed class SelfFactoryPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public SelfFactoryPhantomProfileModelValidator()
        {
            RuleSet("A", () => RuleFor(x => x.Name).NotEmpty());
            When(_ => false, () => Include(_ => new SelfFactoryPhantomProfileModelValidator()));
        }
    }

    /// <summary>Includes <see cref="IncludedPhantomProfileModelValidator"/> and declares nothing else.</summary>
    public sealed class MiddleIncludePhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public MiddleIncludePhantomProfileModelValidator() => Include(new IncludedPhantomProfileModelValidator());
    }

    public sealed class NestedIncludePhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public NestedIncludePhantomProfileModelValidator() => Include(new MiddleIncludePhantomProfileModelValidator());
    }

    /// <summary>One rule, in the ruleset its constructor names.</summary>
    public sealed class RuleSetPartValidator : AbstractValidator<PhantomProfileModel>
    {
        public RuleSetPartValidator(string ruleSet) =>
            RuleSet(ruleSet, () => RuleFor(x => x.Name).NotEmpty());
    }

    public sealed class HeldSiblingIncludesPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public HeldSiblingIncludesPhantomProfileModelValidator()
        {
            Include(new RuleSetPartValidator("A"));
            Include(new RuleSetPartValidator("B"));
        }
    }

    public sealed class FactorySiblingIncludesPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public FactorySiblingIncludesPhantomProfileModelValidator()
        {
            Include(_ => new RuleSetPartValidator("A"));
            Include(_ => new RuleSetPartValidator("B"));
        }
    }

    /// <summary>Above depth zero, a ruleset-A rule and an include of the next depth down; at depth zero, a ruleset-B rule.</summary>
    public sealed class NestingPartValidator : AbstractValidator<PhantomProfileModel>
    {
        public NestingPartValidator(int depth)
        {
            if (depth > 0)
            {
                RuleSet("A", () => RuleFor(x => x.Name).NotEmpty());
                Include(new NestingPartValidator(depth - 1));
            }
            else
            {
                RuleSet("B", () => RuleFor(x => x.Code).NotEmpty());
            }
        }
    }

    public sealed class NestedSameTypeIncludePhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public NestedSameTypeIncludePhantomProfileModelValidator() => Include(new NestingPartValidator(1));
    }

    public sealed class HandRolledIncludePhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public HandRolledIncludePhantomProfileModelValidator() => Include(new HandRolledPhantomProfileModelValidator());
    }

    public sealed class HandRolledPhantomProfileModelValidator : IValidator<PhantomProfileModel>
    {
        public ValidationResult Validate(PhantomProfileModel instance) => new();

        public Task<ValidationResult> ValidateAsync(PhantomProfileModel instance, CancellationToken cancellation = default) =>
            Task.FromResult(new ValidationResult());

        public ValidationResult Validate(IValidationContext context) => new();

        public Task<ValidationResult> ValidateAsync(IValidationContext context, CancellationToken cancellation = default) =>
            Task.FromResult(new ValidationResult());

        public IValidatorDescriptor CreateDescriptor() => throw new NotSupportedException();

        public bool CanValidateInstancesOfType(Type type) => type == typeof(PhantomProfileModel);
    }

    /// <summary>The stock factory's selector, except that it refuses a question about <see cref="PhantomProfileModel"/> asked without a model.</summary>
    private sealed class ModelReadingSelector(IValidatorSelector stock) : IValidatorSelector
    {
        public bool CanExecute(IValidationRule rule, string propertyPath, IValidationContext context) =>
            context is ValidationContext<PhantomProfileModel> { InstanceToValidate: null }
                ? throw new InvalidOperationException("This selector reads the model.")
                : stock.CanExecute(rule, propertyPath, context);
    }
}
