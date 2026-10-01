using System.Collections.Concurrent;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Results;
using Formidable.Tests.Fixtures;

namespace Formidable.Tests;

/// <summary>
/// The Trace line a profile that leaves out the default rules writes when it selects no rule of a
/// plain validator. The class attaches a TraceListener and counts rules through FluentValidation's
/// global selector factory, both process-global, hence the collection. The line is written once per
/// validator type and profile for the life of the process, so every test validates under a profile
/// of its own (a name and a ruleset no other test uses) or through a validator type of its own.
/// Captured lines are filtered to those naming <see cref="PhantomProfileModel"/> (every validator
/// here carries it in its type name), a model no other test class uses.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public class EmptyProfileSelectionTraceTests
{
    /// <summary>A profile that leaves out the default rules and names one ruleset no validator here declares.</summary>
    private static ValidationProfile PhantomProfile(string name) =>
        ValidationProfile.Named(name, includeDefaultRules: false, name + "Set");

    // Mutations: drop the once-latch (four lines); invert the zero test (no line); key the record
    // by reference rather than by full shape (the equal profile writes a second line).
    [Fact]
    public async Task A_profile_selecting_no_rule_is_traced_once()
    {
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator());
        var model = new PhantomProfileModel();
        var phantom = PhantomProfile("TracedOnce");

        // Equal to phantom by full shape (names compare case-insensitively), but its own instance.
        var equal = ValidationProfile.Named("tracedonce", includeDefaultRules: false, "tracedonceset");

        var lines = await TraceCapture.RunAsync(async () =>
        {
            await adapter.ValidateAsync(model, phantom);
            await adapter.ValidateAsync(model, phantom);
            var rules = adapter.SelectRules(equal);
            Assert.Empty(rules);
            await adapter.ValidateRulesAsync(model, equal, rules);
        }, NamesModel);

        var line = Assert.Single(lines);
        Assert.Contains("'TracedOnce'", line);
        Assert.Contains("'TracedOnceSet'", line);
        Assert.Contains(nameof(PlainPhantomProfileModelValidator), line);
    }

    // Mutation: key the record by validator type alone rather than by type and profile, so the
    // second profile stays silent.
    [Fact]
    public async Task A_second_profile_selecting_no_rule_is_traced_on_its_own()
    {
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator());
        var model = new PhantomProfileModel();

        var lines = await TraceCapture.RunAsync(async () =>
        {
            await adapter.ValidateAsync(model, PhantomProfile("FirstOfTwo"));
            await adapter.ValidateAsync(model, PhantomProfile("SecondOfTwo"));
        }, NamesModel);

        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, line => line.Contains("'FirstOfTwo'"));
        Assert.Contains(lines, line => line.Contains("'SecondOfTwo'") && line.Contains("'SecondOfTwoSet'"));
    }

    // Mutation: drop the report from one entry point (the Validate or ValidateAsync extension the
    // adapter's own two members call, or the adapter's ruleset check that SelectRules,
    // ValidateRulesAsync and the inspection members share), so that member's row finds no line.
    [Theory]
    [InlineData("ValidateAsync")]
    [InlineData("Validate")]
    [InlineData("SelectRules")]
    [InlineData("ValidateRulesAsync")]
    [InlineData("GetFieldRequirement")]
    [InlineData("GetDeclaredFieldPaths")]
    public async Task The_first_call_through_each_profile_taking_member_traces(string member)
    {
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator());
        var model = new PhantomProfileModel();
        var phantom = PhantomProfile("Via" + member);

        var lines = await TraceCapture.RunAsync(async () =>
        {
            switch (member)
            {
                case "ValidateAsync":
                    await adapter.ValidateAsync(model, phantom);
                    break;
                case "Validate":
                    adapter.Validate(model, phantom);
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
        }, NamesModel);

        Assert.Single(lines);
    }

    // A caller holding a plain validator reaches the profile extensions without any adapter, and
    // gets the same line.
    // Mutation: report from the adapter only, so the extensions check the ruleset names alone and
    // this call writes nothing.
    [Fact]
    public async Task A_direct_caller_of_the_profile_extensions_is_traced()
    {
        var validator = new PlainPhantomProfileModelValidator();
        var profile = PhantomProfile("DirectCaller");

        var lines = await TraceCapture.RunAsync(async () =>
        {
            var result = await validator.ValidateAsync(new PhantomProfileModel(), profile);
            Assert.True(result.IsValid);
        }, NamesModel);

        var line = Assert.Single(lines);
        Assert.Contains("'DirectCaller'", line);
        Assert.Contains(nameof(PlainPhantomProfileModelValidator), line);
    }

    // The minimal-API shape: the server resolves a new adapter per request over the one validator
    // the container holds.
    // Mutation: keep the record on the adapter, so the second adapter writes the line again.
    [Fact]
    public async Task Two_adapters_over_one_validator_trace_once()
    {
        var validator = new PlainPhantomProfileModelValidator();
        var model = new PhantomProfileModel();
        var profile = PhantomProfile("TwoAdapters");

        var lines = await TraceCapture.RunAsync(async () =>
        {
            await new FluentValidationModelValidator<PhantomProfileModel>(validator).ValidateAsync(model, profile);
            await new FluentValidationModelValidator<PhantomProfileModel>(validator).ValidateAsync(model, profile);
        }, NamesModel);

        Assert.Single(lines);
    }

    // A scoped validator: every request builds a new instance of the one type, inside a new adapter.
    // Mutation: key the record by validator instance, so the second instance writes the line again.
    [Fact]
    public async Task Two_validators_of_one_type_trace_once()
    {
        var model = new PhantomProfileModel();
        var profile = PhantomProfile("TwoInstances");

        var lines = await TraceCapture.RunAsync(async () =>
        {
            await new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator())
                .ValidateAsync(model, profile);
            await new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator())
                .ValidateAsync(model, profile);
        }, NamesModel);

        Assert.Single(lines);
    }

    // A pin, not a red-first test: each validator type is judged on its own rules, so one profile
    // that selects nothing of two types names each of them.
    // Mutation: key the record by profile alone, so the second type stays silent.
    [Fact]
    public async Task Two_validator_types_under_one_profile_each_trace()
    {
        var model = new PhantomProfileModel();
        var profile = PhantomProfile("TwoTypes");

        var lines = await TraceCapture.RunAsync(async () =>
        {
            await new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator())
                .ValidateAsync(model, profile);
            await new FluentValidationModelValidator<PhantomProfileModel>(new SecondPhantomProfileModelValidator())
                .ValidateAsync(model, profile);
        }, NamesModel);

        Assert.Equal(2, lines.Count);
        Assert.Single(lines, line => line.Contains(nameof(PlainPhantomProfileModelValidator)));
        Assert.Single(lines, line => line.Contains(nameof(SecondPhantomProfileModelValidator)));
    }

    // Mutation: invert the zero test, so a profile that selects rules is traced.
    [Fact]
    public async Task A_profile_that_selects_rules_is_not_traced()
    {
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator());
        var submitOnly = ValidationProfile.Named("SubmitOnly", includeDefaultRules: false, ValidationProfile.SubmitRuleSetName);

        var lines = await TraceCapture.RunAsync(async () =>
        {
            await adapter.ValidateAsync(new PhantomProfileModel(), submitOnly);
            Assert.NotEmpty(adapter.SelectRules(submitOnly));
        }, NamesModel);

        Assert.Empty(lines);
    }

    // The validator has no default rule, so this selection is empty too: only the
    // IncludeDefaultRules test keeps it silent.
    // Mutation: drop the IncludeDefaultRules test, so this profile is traced.
    [Fact]
    public async Task A_profile_including_default_rules_is_not_traced()
    {
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new SubmitOnlyPhantomProfileModelValidator());
        var profile = ValidationProfile.Named("P", includeDefaultRules: true, "NoSuchSet");

        var lines = await TraceCapture.RunAsync(async () =>
        {
            Assert.Empty(adapter.SelectRules(profile));
            await adapter.ValidateAsync(new PhantomProfileModel(), profile);
        }, NamesModel);

        Assert.Empty(lines);
    }

    // Each round races several threads through the first call under a profile of the round's own,
    // lined up on a barrier. Every thread holds its own adapter over its own instance of one
    // validator type, as a server with a scoped validator does, and every round must write exactly
    // one line.
    // Mutation: replace the concurrent record with a plain HashSet. Whether a run catches it is a
    // race, so one green run under the mutation proves nothing.
    [Fact]
    public void Concurrent_first_calls_trace_once()
    {
        const int rounds = 300;
        const int threads = 8;
        var failures = new ConcurrentQueue<Exception>();

        var lines = TraceCapture.Run(() =>
        {
            for (var round = 0; round < rounds; round++)
            {
                var model = new PhantomProfileModel();
                var profile = PhantomProfile("Concurrent" + round);
                using var barrier = new Barrier(threads);

                var workers = Enumerable.Range(0, threads)
                    .Select(_ => new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator()))
                    .Select(adapter => new Thread(() =>
                    {
                        try
                        {
                            barrier.SignalAndWait();
                            adapter.Validate(model, profile);
                        }
                        catch (Exception exception)
                        {
                            failures.Enqueue(exception);
                        }
                    }))
                    .ToList();

                workers.ForEach(worker => worker.Start());
                workers.ForEach(worker => worker.Join());
            }
        }, NamesModel);

        Assert.Empty(failures);
        Assert.Equal(rounds, lines.Count);
    }

    // Mutation: drop the ProfiledValidator test, so the validator is judged as a plain one: nothing
    // throws, and the line is written.
    [Fact]
    public async Task A_ProfiledValidator_still_throws_and_is_not_traced()
    {
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new ProfiledPhantomProfileModelValidator());

        var lines = await TraceCapture.RunAsync(
            async () => await Assert.ThrowsAsync<InvalidOperationException>(
                () => adapter.ValidateAsync(new PhantomProfileModel(), PhantomProfile("ProfiledThrows"))),
            NamesModel);

        Assert.Empty(lines);
    }

    // A ProfiledValidator that registers a ruleset with no rule in it: the profile passes the
    // ruleset check and selects nothing, and the ProfiledValidator test alone keeps it silent.
    // Mutation: drop the ProfiledValidator test, so this profile is traced.
    [Fact]
    public async Task A_ProfiledValidator_with_an_empty_ruleset_is_not_traced()
    {
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new ProfiledPhantomProfileModelValidator());
        var emptyOnly = ValidationProfile.Named("EmptyOnly", includeDefaultRules: false, "Empty");

        var lines = await TraceCapture.RunAsync(async () =>
        {
            Assert.Empty(adapter.SelectRules(emptyOnly));
            await adapter.ValidateAsync(new PhantomProfileModel(), emptyOnly);
        }, NamesModel);

        Assert.Empty(lines);
    }

    // A replaced selector factory whose selector reads the model cannot answer the model-less
    // count. The count gives up without writing a line, and the call validates and returns its
    // report.
    // The factory is process-wide and classes outside the collection walk model-less selections
    // meanwhile, so the installed selector refuses only this class's model and hands every other
    // question to the stock selector.
    // Mutation: drop the try/catch around the count, so ValidateAsync throws where it validated.
    [Fact]
    public async Task A_selector_that_needs_a_model_leaves_the_call_working_and_writes_nothing()
    {
        var stock = ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory;
        try
        {
            ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory =
                ruleSets => new ModelReadingSelector(stock(ruleSets));
            var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new PlainPhantomProfileModelValidator());

            ValidationReport? report = null;
            var lines = await TraceCapture.RunAsync(
                async () => report = await adapter.ValidateAsync(new PhantomProfileModel(), PhantomProfile("ModelReadingSelector")),
                NamesModel);

            Assert.NotNull(report);
            Assert.True(report.IsValid);
            Assert.Empty(lines);
        }
        finally
        {
            ValidatorOptions.Global.ValidatorSelectors.RulesetValidatorSelectorFactory = stock;
        }
    }

    // Mutation: count a validator that cannot list its rules as selecting none, so it is traced.
    [Fact]
    public async Task A_hand_rolled_validator_is_not_traced()
    {
        var adapter = new FluentValidationModelValidator<PhantomProfileModel>(new HandRolledPhantomProfileModelValidator());
        var model = new PhantomProfileModel();
        var profile = PhantomProfile("HandRolled");

        var lines = await TraceCapture.RunAsync(async () =>
        {
            await adapter.ValidateAsync(model, profile);
            adapter.Validate(model, profile);
            adapter.GetDeclaredFieldPaths(profile);
        }, NamesModel);

        Assert.Empty(lines);
    }

    /// <summary>Whether a Trace line names this class's model.</summary>
    private static bool NamesModel(string line) => line.Contains(nameof(PhantomProfileModel));

    public sealed class PhantomProfileModel
    {
        public string Name { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;
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

    /// <summary>The same rules as <see cref="PlainPhantomProfileModelValidator"/>, under a type of its own.</summary>
    public sealed class SecondPhantomProfileModelValidator : AbstractValidator<PhantomProfileModel>
    {
        public SecondPhantomProfileModelValidator()
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

    public sealed class ProfiledPhantomProfileModelValidator : ProfiledValidator<PhantomProfileModel>
    {
        protected override void ConfigureCommonRules() => RuleFor(x => x.Name).NotEmpty();

        protected override void ConfigureProfiles()
        {
            Profile(ValidationProfile.SubmitRuleSetName, () => RuleFor(x => x.Code).NotEmpty());
            Profile("Empty", () => { });
        }
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
