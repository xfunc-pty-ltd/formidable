using System.Linq.Expressions;
using Bunit;
using Bunit.Rendering;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A form runs a rule written inside FluentValidation's <c>DependentRules</c> wherever
/// FluentValidation itself runs it, and keeps the validator on the rule-by-rule path. An edit to
/// the field a dependent rule names runs the rule carrying it again, so the dependent's message
/// stays current, and a dependent rule's field takes the required mark, "checking" during a
/// submit, and a load's valid state, as any other rule's field does.
/// </summary>
// The mutations these tests name: (1) refuse a dependent in RuleSetSelector.CanExecute, and the
// dependent's message never shows; (2) drop the profile-scope test for a dependent, and a check
// under a narrower LiveProfile serves the submit profile's answer, or the other way round;
// (3) drop the DependentRules walk from WalkDeclaredRule, and the mark, "checking" and the load's
// valid state leave the dependent's field; (4) make SetVerdict.IsFreshFor ignore the edit stamp,
// and an edit is answered from before it, so the rule carrying the dependent does not run again.
public class DependentRulesTests : BunitContext
{
    private const string DependentMessage = "Customer name is required";
    private const string DescriptionMessage = "Description is required";
    private const string ShortMessage = "Description needs five letters";
    private const string ReferenceMessage = "Reference is required";

    private readonly FakeTimeProvider _clock = new();
    private readonly ITestOutputHelper _output;

    public DependentRulesTests(ITestOutputHelper output)
    {
        _output = output;
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddFormidableBlazor();
    }

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
    public sealed class DependentOrderValidator : AbstractValidator<Order>
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

    /// <summary>Where the dependent rule names its field.</summary>
    public enum Case
    {
        /// <summary>The dependent rule names the field of the rule carrying it.</summary>
        OwnField,

        /// <summary>The dependent rule names another field.</summary>
        OtherField,

        /// <summary>The dependent rule sits inside another dependent rule, and names a third field.</summary>
        DependentOfDependent,
    }

    /// <summary>The three cases, with a count of each run of the rule carrying the dependents.</summary>
    private sealed class CaseValidator : AbstractValidator<Order>
    {
        private int _ownerRuns;

        public CaseValidator(Case shape)
        {
            var owner = RuleFor(x => x.Description)
                .Must(value =>
                {
                    Interlocked.Increment(ref _ownerRuns);
                    return !string.IsNullOrEmpty(value);
                })
                .WithMessage(DescriptionMessage);

            switch (shape)
            {
                case Case.OwnField:
                    owner.DependentRules(() =>
                        RuleFor(x => x.Description).MinimumLength(5).WithMessage(ShortMessage));
                    break;
                case Case.OtherField:
                    owner.DependentRules(() =>
                        RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage));
                    break;
                case Case.DependentOfDependent:
                    owner.DependentRules(() =>
                        RuleFor(x => x.Reference).NotEmpty().WithMessage(ReferenceMessage).DependentRules(() =>
                            RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
                    break;
            }
        }

        public int OwnerRuns => Volatile.Read(ref _ownerRuns);
    }

    private static readonly (string Css, string Path)[] Fields =
    [
        ("description", "Description"),
        ("reference", "Reference"),
        ("name", "Customer.Name"),
    ];

    private static (string Css, string Value)[] StepsFor(Case shape) => shape switch
    {
        Case.OwnField =>
        [
            ("description", "Plan"),
            ("description", "Planning"),
            ("description", string.Empty),
            ("description", "Pl"),
            ("description", "Quarterly plan"),
        ],
        Case.OtherField =>
        [
            ("name", string.Empty),
            ("name", "Grace"),
            ("name", string.Empty),
            ("description", string.Empty),
            ("description", "Plan"),
            ("name", "Ada"),
        ],
        _ =>
        [
            ("name", string.Empty),
            ("name", "Grace"),
            ("name", string.Empty),
            ("reference", string.Empty),
            ("reference", "Q4"),
            ("description", string.Empty),
            ("description", "Plan"),
            ("name", "Ada"),
        ],
    };

    // For each case: an edit to the field a dependent rule names runs the rule carrying it again
    // (its count moves on every edit), and after every edit each field shows exactly the messages
    // FluentValidation's own answer holds for it, among the fields the form discloses (those
    // edited, and after a submit every field on screen). Run before any submit, and after a
    // submit, where every edit also re-checks the whole form. Mutation (1) above leaves the
    // dependent's message off its field. Mutation (4) serves each edit the answer from before it,
    // so a dependent's message goes stale.
    [Theory]
    [InlineData(Case.OwnField, false)]
    [InlineData(Case.OtherField, false)]
    [InlineData(Case.DependentOfDependent, false)]
    [InlineData(Case.OwnField, true)]
    [InlineData(Case.OtherField, true)]
    [InlineData(Case.DependentOfDependent, true)]
    public async Task An_edit_keeps_a_dependent_rules_message_current(Case shape, bool afterSubmit)
    {
        var validator = new CaseValidator(shape);
        var oracle = new CaseValidator(shape);
        var model = new Order { Customer = new Customer { Name = "Ada" } };
        var cut = RenderForm(model, validator, requiredMark: false);
        var engine = cut.FindComponent<FormidableForm<Order>>().Instance.Engine!;

        var adapter = Assert.IsAssignableFrom<IRuleLevelValidator<Order>>(Services.GetRequiredService<IModelValidator<Order>>());
        _output.WriteLine($"{shape} afterSubmit={afterSubmit}: CanValidateByRule={adapter.CanValidateByRule}");

        var disclosed = new HashSet<string>(StringComparer.Ordinal);
        if (afterSubmit)
        {
            await cut.InvokeAsync(() => cut.Find("form").Submit());
            await SettleAsync(cut, () => engine.HasSubmitted && !engine.IsValidating);
            disclosed.UnionWith(Fields.Select(field => field.Css));
        }

        var step = 0;
        foreach (var (css, value) in StepsFor(shape))
        {
            step++;
            var runsBefore = validator.OwnerRuns;
            await cut.InvokeAsync(() => cut.Find($"div.{css} input").Change(value));
            await SettleAsync(cut, () => !engine.IsValidating);
            disclosed.Add(css);

            var expected = (await oracle.ValidateAsync(model, options => options.IncludeRulesNotInRuleSet().IncludeRuleSets("Submit")))
                .Errors.ToLookup(failure => failure.PropertyName, failure => failure.ErrorMessage);
            var line = $"{shape} afterSubmit={afterSubmit} step {step}: {css}='{value}' owner runs +{validator.OwnerRuns - runsBefore}";
            foreach (var (fieldCss, path) in Fields)
            {
                var shown = cut.Find($"div.{fieldCss}").QuerySelectorAll("li").Select(item => item.TextContent).ToList();
                line += $" | {fieldCss}: [{string.Join("; ", shown)}]";
                Assert.Equal(disclosed.Contains(fieldCss) ? expected[path].ToList() : [], shown);
                if (disclosed.Contains(fieldCss))
                {
                    Assert.Equal(expected[path].Any(), engine.GetFieldState(FieldOf(model, path)).HasErrors);
                }

                // On the rule-by-rule path every settled edit leaves the submit rules answered,
                // so "would pass submit" follows the same answer on every field.
                if (adapter.CanValidateByRule)
                {
                    Assert.Equal(!expected[path].Any(), engine.GetFieldState(FieldOf(model, path)).WouldPassSubmit);
                }
            }

            _output.WriteLine(line);
            Assert.True(validator.OwnerRuns > runsBefore, $"step {step}: the edit to {css} did not run the rule carrying the dependents");
        }

        await Services.DisposeAsync();
    }

    // The capability stays on for a validator whose own rules carry dependents, in each case.
    [Theory]
    [InlineData(Case.OwnField)]
    [InlineData(Case.OtherField)]
    [InlineData(Case.DependentOfDependent)]
    public void A_validator_with_dependent_rules_keeps_the_rule_by_rule_path(Case shape) =>
        Assert.True(new FluentValidationModelValidator<Order>(new CaseValidator(shape)).CanValidateByRule);

    // After a submit an edit is answered by the edit's own check and by the whole-form re-check
    // that follows it, and the second takes the first's answers, so the rule carrying a dependent
    // runs once for the edit. That holds where the live check runs Draft too, since the dependent
    // sits in no ruleset, as the rule carrying it does. A validator checked whole runs it twice.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task After_a_submit_one_edit_runs_the_rule_carrying_a_dependent_once(bool draftLive)
    {
        var validator = new CaseValidator(Case.OtherField);
        var model = new Order { Customer = new Customer { Name = "Ada" } };
        var cut = RenderForm(
            model, validator, requiredMark: false,
            draftLive ? new FormidableOptions { LiveProfile = ValidationProfile.Draft } : null);
        var engine = cut.FindComponent<FormidableForm<Order>>().Instance.Engine!;

        await cut.InvokeAsync(() => cut.Find("form").Submit());
        await SettleAsync(cut, () => engine.HasSubmitted && !engine.IsValidating);

        foreach (var value in new[] { string.Empty, "Grace" })
        {
            var runsBefore = validator.OwnerRuns;
            await cut.InvokeAsync(() => cut.Find("div.name input").Change(value));
            await SettleAsync(cut, () => !engine.IsValidating);
            _output.WriteLine($"name='{value}': owner runs +{validator.OwnerRuns - runsBefore}");
            Assert.Equal(1, validator.OwnerRuns - runsBefore);
        }

        await Services.DisposeAsync();
    }

    // A dependent rule's field is a declared field, so its presence rule under a carrying rule
    // whose every failure blocks a submit gives it the required mark. Mutation (3) above.
    [Fact]
    public async Task A_dependent_rules_field_takes_the_required_mark()
    {
        var model = new Order { Customer = new Customer { Name = "Ada" } };
        var cut = RenderForm(model, new DependentOrderValidator(), requiredMark: true);
        var engine = cut.FindComponent<FormidableForm<Order>>().Instance.Engine!;

        Assert.Equal(FieldRequirement.Required, engine.GetFieldRequirement(new FieldIdentifier(model.Customer, nameof(Customer.Name))));
        Assert.Single(cut.FindAll("div.name span.formidable-required"));
        Assert.Single(cut.FindAll("div.description span.formidable-required"));

        await Services.DisposeAsync();
    }

    // A submit shows "checking" on every field a rule it runs is declared for, a dependent rule's
    // field included, while that rule awaits. Mutation (3) above.
    [Fact]
    public async Task A_submit_shows_checking_on_a_dependent_rules_field()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fluent = new InlineValidator<Order>();
        fluent.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
            fluent.RuleFor(x => x.Customer.Name).MustAsync(async (name, _) => await gate.Task && !string.IsNullOrEmpty(name))
                .WithMessage(DependentMessage));
        var model = new Order { Customer = new Customer { Name = "Ada" } };
        using var engine = new FormidableEngine<Order>(
            model, new EditContext(model), new FluentValidationModelValidator<Order>(fluent),
            new ReflectionModelIntrospector(), new FormidableOptions(), _clock, EngineTestSync.OneAtATime());
        var name = new FieldIdentifier(model.Customer, nameof(Customer.Name));

        var submit = engine.ValidateForSubmitAsync();
        Assert.True(engine.IsValidating);
        _output.WriteLine($"while the submit waits: Customer.Name IsValidating={engine.GetFieldState(name).IsValidating}");
        Assert.True(engine.GetFieldState(name).IsValidating);

        gate.SetResult(true);
        var outcome = await submit.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(outcome.Report.IsValid);
        Assert.False(engine.GetFieldState(name).IsValidating);
    }

    // A load engages a declared field that holds a value, so a dependent rule's field that loads a
    // passing value turns valid as any other does. Mutation (3) above.
    [Fact]
    public async Task A_load_engages_a_dependent_rules_field_that_holds_a_value()
    {
        var model = new Order { Customer = new Customer { Name = "Ada" } };
        using var engine = new FormidableEngine<Order>(
            model, new EditContext(model), new FluentValidationModelValidator<Order>(new DependentOrderValidator()),
            new ReflectionModelIntrospector(), new FormidableOptions(), _clock);
        var name = new FieldIdentifier(model.Customer, nameof(Customer.Name));

        await engine.DiscloseLoadedValuesAsync();

        var state = engine.GetFieldState(name);
        _output.WriteLine($"after the load: Customer.Name IsTouched={state.IsTouched} HasErrors={state.HasErrors} WouldPassSubmit={state.WouldPassSubmit}");
        Assert.True(state.IsTouched);
        Assert.False(state.HasErrors);
        Assert.True(state.WouldPassSubmit);
    }

    // A dependent in a ruleset its carrying rule is not in runs under Submit and not under Draft,
    // so an answer the narrower live check gives is never served to the submit profile's check,
    // nor the other way round: the live check shows nothing on the dependent's field, and
    // IsFormValid reports the dependent's failure. Mutation (2) above breaks one or the other.
    [Fact]
    public async Task A_narrower_live_check_and_the_validity_check_keep_their_own_answers_for_a_dependent()
    {
        var fluent = new InlineValidator<Order>();
        fluent.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
            fluent.RuleSet("Submit", () =>
                fluent.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
        var model = new Order { Customer = new Customer { Name = "Ada" } };
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<Order>(
            model, editContext, new FluentValidationModelValidator<Order>(fluent), new ReflectionModelIntrospector(),
            new FormidableOptions { LiveProfile = ValidationProfile.Draft, TrackFormValidity = true }, _clock);
        var name = new FieldIdentifier(model.Customer, nameof(Customer.Name));
        var description = new FieldIdentifier(model, nameof(Order.Description));

        // A first edit the whole form passes, so IsFormValid is seen to answer at all.
        model.Description = "Plan";
        editContext.NotifyFieldChanged(description);
        Assert.True(await UntilAdvancedAsync(() => engine.IsFormValid && !engine.IsValidating));

        model.Customer.Name = string.Empty;
        editContext.NotifyFieldChanged(name);
        var answered = await UntilAdvancedAsync(() => !engine.IsFormValid && !engine.IsValidating);
        _output.WriteLine($"after the name edit: IsFormValid={engine.IsFormValid} live messages on Customer.Name=[{string.Join("; ", editContext.GetValidationMessages(name))}]");
        Assert.True(answered, "IsFormValid did not report the dependent's failure");
        Assert.Empty(editContext.GetValidationMessages(name));
    }

    /// <summary>An untagged rule on Description, counting its runs, carrying a rule on the customer's name inside the Submit ruleset, so Draft runs the first and Submit both.</summary>
    private sealed class TaggedDependentValidator : AbstractValidator<Order>
    {
        private int _ownerRuns;

        public TaggedDependentValidator() =>
            RuleFor(x => x.Description)
                .Must(value =>
                {
                    Interlocked.Increment(ref _ownerRuns);
                    return !string.IsNullOrEmpty(value);
                })
                .WithMessage(DescriptionMessage)
                .DependentRules(() =>
                    RuleSet("Submit", () =>
                        RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));

        public int OwnerRuns => Volatile.Read(ref _ownerRuns);
    }

    /// <summary>An engine whose live check runs Draft, with <see cref="FormidableOptions.TrackFormValidity"/> off.</summary>
    private FormidableEngine<Order> NarrowLiveEngine(Order model, EditContext editContext, TaggedDependentValidator validator) =>
        new(model, editContext, new FluentValidationModelValidator<Order>(validator), new ReflectionModelIntrospector(),
            new FormidableOptions { LiveProfile = ValidationProfile.Draft, TrackFormValidity = false }, _clock,
            EngineTestSync.OneAtATime());

    /// <summary>Commits <paramref name="value"/> to the customer's name and waits, without moving the clock, until the live check it starts has run the rule on Description and ended.</summary>
    private static async Task EditNameAsync(
        FormidableEngine<Order> engine, EditContext editContext, Order model, TaggedDependentValidator validator, string value)
    {
        var runsBefore = validator.OwnerRuns;
        model.Customer.Name = value;
        editContext.NotifyFieldChanged(new FieldIdentifier(model.Customer, nameof(Customer.Name)));
        Assert.True(
            await EngineTestSync.UntilAsync(() => validator.OwnerRuns > runsBefore && !engine.IsValidating),
            "the live check did not run the rule on Description");
    }

    // With TrackFormValidity off and a narrower LiveProfile, nothing before a submit vouches that
    // a field would pass one. The live check runs Draft, which leaves out the rule on the
    // customer's name, and its answer never stands in for the submit profile's: the emptied name
    // does not read as passing a submit that would fail it. Mutation (2) above serves the live
    // check's answer to the valid state, and the name reads as passing.
    [Fact]
    public async Task Before_a_submit_a_narrower_live_check_never_vouches_for_a_dependent_rules_field()
    {
        var validator = new TaggedDependentValidator();
        var model = new Order { Description = "Plan", Customer = new Customer { Name = "Ada" } };
        var editContext = new EditContext(model);
        using var engine = NarrowLiveEngine(model, editContext, validator);
        var name = new FieldIdentifier(model.Customer, nameof(Customer.Name));

        await EditNameAsync(engine, editContext, model, validator, string.Empty);

        var submitAnswer = (await new TaggedDependentValidator().ValidateAsync(model, options => options.IncludeRulesNotInRuleSet().IncludeRuleSets("Submit")))
            .Errors.Select(failure => failure.ErrorMessage).ToList();
        var state = engine.GetFieldState(name);
        _output.WriteLine($"after the name edit: FluentValidation under Submit=[{string.Join("; ", submitAnswer)}] live messages=[{string.Join("; ", editContext.GetValidationMessages(name))}] WouldPassSubmit={state.WouldPassSubmit}");
        Assert.Equal([DependentMessage], submitAnswer);
        Assert.Empty(editContext.GetValidationMessages(name));
        Assert.False(state.WouldPassSubmit);
    }

    // After a submit the name blocked, with TrackFormValidity off and a narrower LiveProfile, the
    // whole-form re-check after each edit runs the rule the live check leaves out: filling the
    // name clears its message and turns it valid, and emptying it again brings the message back.
    // Between each edit and its re-check the name does not read as passing, since only the
    // re-check answers the submit rules for its new value. Mutation (2) above serves the live
    // check's answer to the valid state, and the filled name reads as passing before its re-check.
    [Fact]
    public async Task After_a_submit_the_re_check_runs_a_dependent_rule_a_narrower_live_check_left_out()
    {
        var validator = new TaggedDependentValidator();
        var model = new Order { Description = "Plan", Customer = new Customer { Name = string.Empty } };
        var cut = RenderForm(
            model, validator, requiredMark: false,
            new FormidableOptions { LiveProfile = ValidationProfile.Draft, TrackFormValidity = false });
        var engine = cut.FindComponent<FormidableForm<Order>>().Instance.Engine!;
        var name = new FieldIdentifier(model.Customer, nameof(Customer.Name));

        await cut.InvokeAsync(() => cut.Find("form").Submit());
        cut.WaitForState(() => engine.HasSubmitted && !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });
        Assert.Equal([DependentMessage], Shown(cut, "name"));

        await EditAndReCheckAsync("Grace");
        var filled = engine.GetFieldState(name);
        _output.WriteLine($"after 'Grace' and its re-check: name messages=[{string.Join("; ", Shown(cut, "name"))}] WouldPassSubmit={filled.WouldPassSubmit}");
        Assert.Empty(Shown(cut, "name"));
        Assert.True(filled.WouldPassSubmit);

        await EditAndReCheckAsync(string.Empty);
        var emptied = engine.GetFieldState(name);
        _output.WriteLine($"after '' and its re-check: name messages=[{string.Join("; ", Shown(cut, "name"))}] WouldPassSubmit={emptied.WouldPassSubmit}");
        Assert.Equal([DependentMessage], Shown(cut, "name"));
        Assert.True(emptied.HasErrors);
        Assert.False(emptied.WouldPassSubmit);

        await Services.DisposeAsync();

        // Commits the value and waits, without moving the clock, for the live check it starts; the
        // name must not read as passing yet. Then moves the clock past RefreshDebounce and waits for
        // the re-check to run the rule on Description.
        async Task EditAndReCheckAsync(string value)
        {
            var runsBefore = validator.OwnerRuns;
            await cut.InvokeAsync(() => cut.Find("div.name input").Change(value));
            cut.WaitForState(() => validator.OwnerRuns > runsBefore && !engine.IsValidating, TimeSpan.FromSeconds(5));
            await cut.InvokeAsync(() => { });
            var beforeReCheck = engine.GetFieldState(name);
            _output.WriteLine($"'{value}' before its re-check: name messages=[{string.Join("; ", Shown(cut, "name"))}] WouldPassSubmit={beforeReCheck.WouldPassSubmit}");
            Assert.False(beforeReCheck.WouldPassSubmit, $"'{value}' read as passing before its re-check");

            runsBefore = validator.OwnerRuns;
            await cut.InvokeAsync(() => _clock.Advance(new FormidableOptions().RefreshDebounce + TimeSpan.FromMilliseconds(1)));
            cut.WaitForState(() => validator.OwnerRuns > runsBefore && !engine.IsValidating, TimeSpan.FromSeconds(5));
            await cut.InvokeAsync(() => { });
        }
    }

    private static List<string> Shown(IRenderedComponent<ContainerFragment> cut, string css) =>
        cut.Find($"div.{css}").QuerySelectorAll("li").Select(item => item.TextContent).ToList();

    [Fact]
    public async Task A_submit_is_blocked_by_a_dependent_rule()
    {
        var model = new Order();
        var validSubmits = 0;
        SubmitOutcome? blocked = null;
        Services.AddSingleton<IValidator<Order>>(new DependentOrderValidator());
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Order>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "OnValidSubmit", EventCallback.Factory.Create<SubmitOutcome>(this, () => validSubmits++));
            builder.AddComponentParameter(3, "OnInvalidSubmit", EventCallback.Factory.Create<FormidableInvalidSubmitContext>(this, c => blocked = c.Outcome));
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                Expression<Func<string?>> name = () => model.Customer.Name;
                inner.OpenElement(0, "div");
                inner.AddAttribute(1, "class", "name");
                inner.OpenComponent<FormidableInputText>(2);
                inner.AddComponentParameter(3, "Value", model.Customer.Name);
                inner.AddComponentParameter(4, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => model.Customer.Name = v));
                inner.AddComponentParameter(5, "ValueExpression", name);
                inner.CloseComponent();
                inner.OpenComponent<FormidableFieldMessage<string?>>(6);
                inner.AddComponentParameter(7, "For", name);
                inner.CloseComponent();
                inner.CloseElement();
                inner.AddMarkupContent(8, "<button type=\"submit\">Save</button>");
            }));
            builder.CloseComponent();
        });
        var engine = cut.FindComponent<FormidableForm<Order>>().Instance.Engine!;

        await cut.InvokeAsync(() => cut.Find("form").Submit());
        cut.WaitForState(() => engine.HasSubmitted && !engine.IsValidating);
        await cut.InvokeAsync(() => { });

        Assert.Equal(0, validSubmits);
        Assert.NotNull(blocked);
        var issue = Assert.Single(blocked.Report.Issues);
        Assert.Equal("Customer.Name", issue.Path);
        Assert.Equal(DependentMessage, issue.Message);
        Assert.Contains(DependentMessage, cut.Find(".name").TextContent, StringComparison.Ordinal);
    }

    public enum Shape
    {
        // No dependent rule anywhere: the control.
        None,

        // RuleFor(...).DependentRules(...) on one of the validator's own rules.
        OnOwnRule,

        // A dependent rule inside another dependent rule.
        Nested,

        // RuleForEach(...).DependentRules(...).
        OnOwnCollectionRule,

        // The carrying rule inside RuleSet("Submit", ...); its dependent rule takes the tag.
        InSubmitRuleSet,

        // An untagged carrying rule whose dependent rule is declared inside RuleSet("Submit", ...).
        TaggedUnderUntagged,

        // Inside a child validator attached with SetValidator.
        InChildValidator,

        // Inside a child validator attached with rulesets named on the SetValidator call.
        InScopedChildValidator,

        // Inside an Include()d validator.
        InIncluded,

        // Inside a ChildRules block.
        InChildRules,
    }

    private static InlineValidator<Order> Build(Shape shape)
    {
        var validator = new InlineValidator<Order>();
        switch (shape)
        {
            case Shape.None:
                validator.RuleFor(x => x.Description).NotEmpty();
                validator.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage);
                break;
            case Shape.OnOwnRule:
                validator.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                    validator.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage));
                break;
            case Shape.Nested:
                validator.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                    validator.RuleFor(x => x.Reference).NotEmpty().DependentRules(() =>
                        validator.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
                break;
            case Shape.OnOwnCollectionRule:
                validator.RuleForEach(x => x.Lines).NotNull().DependentRules(() =>
                    validator.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage));
                break;
            case Shape.InSubmitRuleSet:
                validator.RuleSet("Submit", () =>
                    validator.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                        validator.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
                break;
            case Shape.TaggedUnderUntagged:
                validator.RuleFor(x => x.Description).NotEmpty().DependentRules(() =>
                    validator.RuleSet("Submit", () =>
                        validator.RuleFor(x => x.Customer.Name).NotEmpty().WithMessage(DependentMessage)));
                break;
            case Shape.InChildValidator:
                validator.RuleFor(x => x.Customer).SetValidator(new DependentCustomerValidator());
                break;
            case Shape.InScopedChildValidator:
                validator.RuleFor(x => x.Customer).SetValidator(new DependentCustomerValidator(), "default");
                break;
            case Shape.InIncluded:
                validator.Include(new DependentOrderValidator());
                break;
            case Shape.InChildRules:
                validator.RuleFor(x => x.Customer).ChildRules(customer =>
                    customer.RuleFor(c => c.Email).NotEmpty().DependentRules(() =>
                        customer.RuleFor(c => c.Name).NotEmpty().WithMessage(DependentMessage)));
                break;
        }

        return validator;
    }

    // Each row is submitted under Draft (the rules outside every ruleset) and under Submit (those
    // plus the "Submit" ruleset), and compared with FluentValidation validating the same model with
    // the same selection spelled in its own terms. Every row keeps the rule-by-rule path, so its
    // equality is that path's.
    [Theory]
    [InlineData(Shape.None)]
    [InlineData(Shape.OnOwnRule)]
    [InlineData(Shape.Nested)]
    [InlineData(Shape.OnOwnCollectionRule)]
    [InlineData(Shape.InSubmitRuleSet)]
    [InlineData(Shape.TaggedUnderUntagged)]
    [InlineData(Shape.InChildValidator)]
    [InlineData(Shape.InScopedChildValidator)]
    [InlineData(Shape.InIncluded)]
    [InlineData(Shape.InChildRules)]
    public async Task A_submit_answers_as_FluentValidation_does_wherever_a_dependent_rule_sits(Shape shape)
    {
        foreach (var withSubmitRuleSet in new[] { false, true })
        {
            var fluent = Build(shape);
            var adapter = new FluentValidationModelValidator<Order>(fluent);
            var model = new Order();
            var own = await fluent.ValidateAsync(model, options =>
            {
                options.IncludeRulesNotInRuleSet();
                if (withSubmitRuleSet)
                {
                    options.IncludeRuleSets("Submit");
                }
            });

            var profile = withSubmitRuleSet ? ValidationProfile.Submit : ValidationProfile.Draft;
            using var engine = new FormidableEngine<Order>(
                model, new EditContext(model), adapter, new ReflectionModelIntrospector(),
                new FormidableOptions { SubmitProfile = profile }, _clock);
            var outcome = await engine.ValidateForSubmitAsync();

            Assert.Equal(
                own.Errors.Select(failure => $"{failure.PropertyName}: {failure.ErrorMessage}").Order(StringComparer.Ordinal),
                outcome.Report.Issues.Select(issue => $"{issue.Path}: {issue.Message}").Order(StringComparer.Ordinal));

            // Every shape fails under Submit, so no row passes on two empty answers there.
            if (withSubmitRuleSet)
            {
                Assert.Contains(outcome.Report.Issues, issue => issue.Message == DependentMessage);
            }

            // Last, so a validator checked whole shows its equality before this fails.
            Assert.True(adapter.CanValidateByRule);
        }
    }

    /// <summary>Renders a form over <paramref name="model"/> with a kit input and its message for Description, Reference and the customer's name, and a Save button.</summary>
    private IRenderedComponent<ContainerFragment> RenderForm(
        Order model, IValidator<Order> validator, bool requiredMark, FormidableOptions? options = null)
    {
        Services.AddSingleton(validator);
        return Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Order>>(0);
            builder.AddComponentParameter(1, "Model", model);
            if (options is not null)
            {
                builder.AddComponentParameter(2, "Options", options);
            }

            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                Field(inner, 0, "description", () => model.Description, v => model.Description = v ?? string.Empty);
                Field(inner, 20, "reference", () => model.Reference, v => model.Reference = v ?? string.Empty);
                Field(inner, 40, "name", () => model.Customer.Name, v => model.Customer.Name = v);
                inner.AddMarkupContent(60, "<button type=\"submit\">Save</button>");
            }));
            builder.CloseComponent();
        });

        void Field(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder inner, int seq, string css, Expression<Func<string?>> accessor, Action<string?> set)
        {
            inner.OpenElement(seq, "div");
            inner.AddAttribute(seq + 1, "class", css);
            if (requiredMark)
            {
                inner.OpenComponent<FormidableRequiredIndicator<string?>>(seq + 2);
                inner.AddComponentParameter(seq + 3, "For", accessor);
                inner.CloseComponent();
            }

            inner.OpenComponent<FormidableInputText>(seq + 4);
            inner.AddComponentParameter(seq + 5, "Value", accessor.Compile()());
            inner.AddComponentParameter(seq + 6, "ValueChanged", EventCallback.Factory.Create<string?>(this, set));
            inner.AddComponentParameter(seq + 7, "ValueExpression", accessor);
            inner.CloseComponent();
            inner.OpenComponent<FormidableFieldMessage<string?>>(seq + 8);
            inner.AddComponentParameter(seq + 9, "For", accessor);
            inner.CloseComponent();
            inner.CloseElement();
        }
    }

    private static FieldIdentifier FieldOf(Order model, string path) => path switch
    {
        "Customer.Name" => new FieldIdentifier(model.Customer, nameof(Customer.Name)),
        _ => new FieldIdentifier(model, path),
    };

    /// <summary>Waits for <paramref name="settled"/>, moves the clock past the whole-form re-check's wait, and waits again.</summary>
    private async Task SettleAsync(IRenderedComponent<ContainerFragment> cut, Func<bool> settled)
    {
        cut.WaitForState(settled, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => _clock.Advance(new FormidableOptions().RefreshDebounce + TimeSpan.FromMilliseconds(1)));
        cut.WaitForState(settled, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });
    }

    /// <summary>Moves the clock in steps past every debounce until <paramref name="condition"/> holds, for at most five seconds.</summary>
    private async Task<bool> UntilAdvancedAsync(Func<bool> condition)
    {
        for (var tries = 0; tries < 50 && !condition(); tries++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(301));
            await Task.Delay(100);
        }

        return condition();
    }
}
