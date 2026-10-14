using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Bunit;
using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A validator that overrides <c>PreValidate</c> to add a failure and carry on has that failure
/// shown once: in the kit's field message and summary, in Blazor's own
/// <see cref="ValidationMessage{TValue}"/> and <see cref="ValidationSummary"/>, and in the
/// <c>OnInvalidSubmit</c> report. FluentValidation runs <c>PreValidate</c> once per validator
/// call and the failure belongs to no rule, so a form running a draft-and-submit validator's two
/// rule groups in two calls would report it twice; such a validator is validated whole instead.
/// </summary>
// The mutation the duplicate cases name: drop the PreValidate test from
// FluentValidationModelValidator.CanValidateByRule (and from RequireRuleLevelCapability), so the
// override keeps the rule-level path. The failure then shows twice on Blazor's own components from
// the first edit, and on every surface after a blocked submit.
public class PreValidateDuplicateTests : BunitContext
{
    public const string PreMessage = "Pre-check says no";
    public const string Blocked = "blocked";

    private static readonly string[] Areas = ["kit-summary", "native-summary", "kit-message", "native-message"];

    private readonly FakeTimeProvider _clock = new();

    public PreValidateDuplicateTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddFormidableBlazor();
    }

    public sealed class Signup
    {
        public string? Name { get; set; } = "Ada";

        public string? Email { get; set; } = "ada@example.com";
    }

    /// <summary>A draft rule on Name and a submit rule on Email, so the submit profile selects two rule groups, and a <c>PreValidate</c> that adds a failure on Name while it starts with "blocked".</summary>
    public sealed class PreCheckedSignupValidator : DraftSubmitValidator<Signup>
    {
        public int PreValidateCalls { get; private set; }

        public int NameRuleRuns { get; private set; }

        protected override void ConfigureDraftRules() =>
            RuleFor(m => m.Name)
                .MustAsync((name, _) =>
                {
                    NameRuleRuns++;
                    return Task.FromResult(name is null || name.Length <= 20);
                })
                .WithMessage("Name is 20 characters max");

        protected override void ConfigureSubmitRules() =>
            RuleFor(m => m.Email).NotEmpty().WithMessage("Email is required");

        protected override bool PreValidate(ValidationContext<Signup> context, FluentValidation.Results.ValidationResult result)
        {
            PreValidateCalls++;
            if (context.InstanceToValidate.Name?.StartsWith(Blocked, StringComparison.Ordinal) == true)
            {
                context.AddFailure(nameof(Signup.Name), PreMessage);
            }

            return true;
        }
    }

    /// <summary>The same rules with no <c>PreValidate</c> override: the control.</summary>
    public sealed class PlainSignupValidator : DraftSubmitValidator<Signup>
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(m => m.Name).MaximumLength(20).WithMessage("Name is 20 characters max");

        protected override void ConfigureSubmitRules() =>
            RuleFor(m => m.Email).NotEmpty().WithMessage("Email is required");
    }

    /// <summary>The same rules, and a <c>ValidateAsync</c> override that appends a failure on Name while it starts with "blocked".</summary>
    public sealed class AppendingSignupValidator : DraftSubmitValidator<Signup>
    {
        protected override void ConfigureDraftRules() =>
            RuleFor(m => m.Name).MaximumLength(20).WithMessage("Name is 20 characters max");

        protected override void ConfigureSubmitRules() =>
            RuleFor(m => m.Email).NotEmpty().WithMessage("Email is required");

        public override async Task<FluentValidation.Results.ValidationResult> ValidateAsync(ValidationContext<Signup> context, CancellationToken cancellation = default)
        {
            var result = await base.ValidateAsync(context, cancellation);
            if (context.InstanceToValidate.Name?.StartsWith(Blocked, StringComparison.Ordinal) == true)
            {
                result.Errors.Add(new FluentValidation.Results.ValidationFailure(nameof(Signup.Name), PreMessage));
            }

            return result;
        }
    }

    /// <summary>Declares no rule of its own and forwards every asynchronous validation to the plain rules.</summary>
    public sealed class ForwardingSignupValidator : AbstractValidator<Signup>
    {
        private readonly PlainSignupValidator _inner = new();

        public override Task<FluentValidation.Results.ValidationResult> ValidateAsync(ValidationContext<Signup> context, CancellationToken cancellation = default) =>
            _inner.ValidateAsync(context, cancellation);
    }

    /// <summary>Counts the whole-profile and rule-level calls a form makes through it.</summary>
    private sealed class CallCountingValidator(IModelValidator<Signup> inner) : DelegatingModelValidator<Signup>(inner)
    {
        public int WholeProfileCalls { get; private set; }

        public int RuleLevelCalls { get; private set; }

        public override Task<ValidationReport> ValidateAsync(Signup model, ValidationProfile profile, CancellationToken cancellationToken = default)
        {
            WholeProfileCalls++;
            return base.ValidateAsync(model, profile, cancellationToken);
        }

        public override Task<RuleLevelResult> ValidateRulesAsync(Signup model, ValidationProfile profile,
            IReadOnlyList<RuleIdentity> rules, CancellationToken cancellationToken = default)
        {
            RuleLevelCalls++;
            return base.ValidateRulesAsync(model, profile, rules, cancellationToken);
        }
    }

    [Fact]
    public async Task A_PreValidate_failure_shows_once_on_every_surface()
    {
        var validator = new PreCheckedSignupValidator();
        var model = new Signup();
        SubmitOutcome? blocked = null;
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Signup>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<Signup>(validator));
            builder.AddComponentParameter(3, "OnInvalidSubmit", EventCallback.Factory.Create<FormidableInvalidSubmitContext>(this, c => blocked = c.Outcome));
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                Expression<Func<string?>> name = () => model.Name;
                inner.OpenElement(0, "div");
                inner.AddAttribute(1, "class", "kit-summary");
                inner.OpenComponent<FormidableSummary>(2);
                inner.CloseComponent();
                inner.CloseElement();
                inner.OpenElement(3, "div");
                inner.AddAttribute(4, "class", "native-summary");
                inner.OpenComponent<ValidationSummary>(5);
                inner.CloseComponent();
                inner.CloseElement();
                inner.OpenElement(6, "div");
                inner.AddAttribute(7, "class", "kit-message");
                inner.OpenComponent<FormidableInputText>(8);
                inner.AddComponentParameter(9, "Value", model.Name);
                inner.AddComponentParameter(10, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => model.Name = v));
                inner.AddComponentParameter(11, "ValueExpression", name);
                inner.CloseComponent();
                inner.OpenComponent<FormidableFieldMessage<string?>>(12);
                inner.AddComponentParameter(13, "For", name);
                inner.CloseComponent();
                inner.CloseElement();
                inner.OpenElement(14, "div");
                inner.AddAttribute(15, "class", "native-message");
                inner.OpenComponent<ValidationMessage<string?>>(16);
                inner.AddComponentParameter(17, "For", name);
                inner.CloseComponent();
                inner.CloseElement();
                inner.AddMarkupContent(18, "<button type=\"submit\">Save</button>");
            }));
            builder.CloseComponent();
        });
        var engine = cut.FindComponent<FormidableForm<Signup>>().Instance.Engine!;

        string Copies() =>
            string.Join(" ", Areas.Select(area =>
                $"{area}={Regex.Matches(cut.Find($".{area}").InnerHtml, Regex.Escape(PreMessage)).Count}"));
        var once = string.Join(" ", Areas.Select(area => $"{area}=1"));

        // Entering and leaving the field, with nothing committed, validates nothing.
        await cut.InvokeAsync(() => engine.MarkTouched(new FieldIdentifier(model, nameof(Signup.Name))));
        await cut.InvokeAsync(() => { });
        Assert.False(engine.IsValidating);
        Assert.Equal(0, validator.PreValidateCalls);
        Assert.Equal(0, validator.NameRuleRuns);

        // The edit's own check: one validator call, so PreValidate and the async rule run once.
        await cut.InvokeAsync(() => cut.Find(".kit-message input").Change(Blocked));
        cut.WaitForState(() => !engine.IsValidating);
        await cut.InvokeAsync(() => { });
        Assert.Equal(once, Copies());
        Assert.Equal(1, validator.PreValidateCalls);
        Assert.Equal(1, validator.NameRuleRuns);

        // The blocked submit: one more call, one copy everywhere and in the report.
        await cut.InvokeAsync(() => cut.Find("form").Submit());
        cut.WaitForState(() => engine.HasSubmitted && !engine.IsValidating);
        await cut.InvokeAsync(() => { });
        Assert.NotNull(blocked);
        Assert.Single(blocked.Report.Issues, issue => issue.Message == PreMessage);
        Assert.Equal(once, Copies());
        Assert.Equal(2, validator.PreValidateCalls);
        Assert.Equal(2, validator.NameRuleRuns);

        // An edit after the submit, through the whole-form re-check that follows it: still one
        // copy on every surface. A validator validated whole runs PreValidate and the async rule
        // in both checks, so the rule runs twice for this one edit (3, then 4), the price the docs
        // state for that path ("after a submit a rule can run twice for one edit").
        await cut.InvokeAsync(() => cut.Find(".kit-message input").Change(Blocked + "!"));
        cut.WaitForState(() => !engine.IsValidating);
        await cut.InvokeAsync(() => { });
        Assert.Equal(once, Copies());
        Assert.Equal(3, validator.PreValidateCalls);
        Assert.Equal(3, validator.NameRuleRuns);
        _clock.Advance(new FormidableOptions().RefreshDebounce);
        cut.WaitForState(() => !engine.IsValidating && validator.PreValidateCalls == 4);
        await cut.InvokeAsync(() => { });
        Assert.Equal(once, Copies());
        Assert.Equal(4, validator.NameRuleRuns);

        // Nothing runs a third time for that edit: neither PreValidate nor the async rule.
        _clock.Advance(TimeSpan.FromSeconds(30));
        await cut.InvokeAsync(() => { });
        Assert.False(engine.IsValidating);
        Assert.Equal(4, validator.PreValidateCalls);
        Assert.Equal(4, validator.NameRuleRuns);
    }

    // Control: the same draft-and-submit rules with no override keep the rule-level path, one
    // call per rule group; the override takes the whole profile in one call per check.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_a_PreValidate_override_leaves_the_rule_level_path(bool overrides)
    {
        IValidator<Signup> fluent = overrides ? new PreCheckedSignupValidator() : new PlainSignupValidator();
        var counting = new CallCountingValidator(new FluentValidationModelValidator<Signup>(fluent));
        var model = new Signup();
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<Signup>(
            model, editContext, counting, new ReflectionModelIntrospector(), new FormidableOptions(), _clock);

        Assert.Equal(!overrides, counting.CanValidateByRule);

        model.Name = Blocked;
        editContext.NotifyFieldChanged(new FieldIdentifier(model, nameof(Signup.Name)));
        Assert.True(await UntilAsync(() => !engine.IsValidating));
        var outcome = await engine.ValidateForSubmitAsync();

        Assert.Equal(overrides ? 1 : 0, outcome.Report.Issues.Count(issue => issue.Message == PreMessage));
        if (overrides)
        {
            Assert.Equal(2, counting.WholeProfileCalls);
            Assert.Equal(0, counting.RuleLevelCalls);
        }
        else
        {
            Assert.Equal(0, counting.WholeProfileCalls);
            Assert.True(counting.RuleLevelCalls >= 2, $"rule-level calls: {counting.RuleLevelCalls}");
        }
    }

    // Every validator call passes through ValidateAsync, so a failure an override appends would be
    // reported once per rule group, and a forwarding override with no rules of its own would be
    // run against an empty selection and validate nothing. Such a validator is validated whole:
    // the appended failure comes back once, and the forwarded rules block the submit.
    // Mutation: drop the ValidateAsync test from CanValidateByRule and RequireRuleLevelCapability,
    // and the appended failure comes back twice while the forwarding validator lets an empty Email
    // through.
    [Theory]
    [InlineData("appending")]
    [InlineData("forwarding")]
    public async Task A_ValidateAsync_override_is_validated_whole(string shape)
    {
        IValidator<Signup> fluent = shape == "appending" ? new AppendingSignupValidator() : new ForwardingSignupValidator();
        var counting = new CallCountingValidator(new FluentValidationModelValidator<Signup>(fluent));
        var model = new Signup { Name = Blocked, Email = shape == "appending" ? "ada@example.com" : string.Empty };
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<Signup>(
            model, editContext, counting, new ReflectionModelIntrospector(), new FormidableOptions(), _clock);

        var outcome = await engine.ValidateForSubmitAsync();

        Assert.False(counting.CanValidateByRule);
        Assert.Equal(0, counting.RuleLevelCalls);
        Assert.False(outcome.CanProceed);
        var expected = shape == "appending" ? PreMessage : "Email is required";
        Assert.Single(outcome.Report.Issues, issue => issue.Message == expected);
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int tries = 300)
    {
        for (var waited = 0; waited < tries; waited++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }
}
