using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A form runs a rule written inside FluentValidation's <c>DependentRules</c> wherever
/// FluentValidation itself runs it. A dependent rule under one of the validator's own rules takes
/// the validator off the rule-by-rule path, so a submit over a model the dependent rule rejects is
/// blocked with its message on its field. Wherever else a dependent rule sits, the form's answer
/// equals FluentValidation's own under each profile, and the rule-by-rule path stays on where it
/// already runs the dependent rule.
/// </summary>
// The mutation both tests name: drop the DependentRules test from
// FluentValidationModelValidator.CanValidateByRule (and from RequireRuleLevelCapability), so a
// validator whose own rule carries a dependent keeps the rule-by-rule path. The dependent rule then
// never runs: the submit proceeds and OnValidSubmit runs, and every row expecting the capability
// off reads it on and answers without the dependent rule's failure under Submit.
public class DependentRulesTests : BunitContext
{
    private const string DependentMessage = "Customer name is required";

    private readonly FakeTimeProvider _clock = new();

    public DependentRulesTests()
    {
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

    [Fact]
    public async Task A_submit_is_blocked_by_a_dependent_rule()
    {
        Services.AddSingleton<IValidator<Order>>(new DependentOrderValidator());
        var model = new Order();
        var validSubmits = 0;
        SubmitOutcome? blocked = null;
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

        // The validator the container hands the form, the adapter AddFormidable registers.
        var resolved = Assert.IsAssignableFrom<IRuleLevelValidator<Order>>(Services.GetRequiredService<IModelValidator<Order>>());
        Assert.False(resolved.CanValidateByRule);

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
    // the same selection spelled in its own terms. A row whose capability stays on is one the
    // rule-by-rule path already answers in full, so its equality is that path's.
    [Theory]
    [InlineData(Shape.None, true)]
    [InlineData(Shape.OnOwnRule, false)]
    [InlineData(Shape.Nested, false)]
    [InlineData(Shape.OnOwnCollectionRule, false)]
    [InlineData(Shape.InSubmitRuleSet, false)]
    [InlineData(Shape.TaggedUnderUntagged, false)]
    [InlineData(Shape.InChildValidator, true)]
    [InlineData(Shape.InScopedChildValidator, true)]
    [InlineData(Shape.InIncluded, true)]
    [InlineData(Shape.InChildRules, true)]
    public async Task A_submit_answers_as_FluentValidation_does_wherever_a_dependent_rule_sits(Shape shape, bool takenRuleByRule)
    {
        foreach (var withSubmitRuleSet in new[] { false, true })
        {
            var fluent = Build(shape);
            var adapter = new FluentValidationModelValidator<Order>(fluent);
            Assert.Equal(takenRuleByRule, adapter.CanValidateByRule);

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
        }
    }
}
