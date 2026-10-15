using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A rule that throws while a submit or a load of values checks the form faults the form instead
/// of throwing to the caller. A submit is blocked: its outcome carries the exception as
/// <see cref="SubmitOutcome.Fault"/>, the form shows <see cref="FormidableOptions.ValidationFaultMessage"/>,
/// <see cref="IFormidableEngine.ValidationFaulted"/> is raised and a warning is logged with the
/// exception. A load returns with the same message, event and warning. On
/// <see cref="FormidableForm{TModel}"/> and <see cref="FormidableValidator{TModel}"/>, the blocked
/// submit runs <c>OnInvalidSubmit</c> and moves focus to the form, as any blocked submit does.
/// </summary>
// Mutations that must break it: send a submit's rule failure back to its caller, and the submit
// rows fail; a load's, and the load row fails; drop the warning, and the logged rows fail; leave
// Fault unset, and the outcome rows fail; drop a refusal's configuration-error mark, or read no
// mark at all, and the refusal rows fail.
public class SubmitFaultTests : BunitContext
{
    private const string ServiceDown = "The address service is down.";

    private readonly FakeTimeProvider _time = new();
    private readonly RecordingFocusService _focus = new();
    private readonly CapturingLoggerProvider _logs = new();

    public SubmitFaultTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IFormidableFocusService>(_focus);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddProvider(_logs)));
        Services.AddFormidableBlazor();
    }

    public sealed class Address
    {
        public string? Street { get; set; } = "1 Main St";
    }

    /// <summary>One async rule on Street that throws <see cref="ServiceDown"/> while <see cref="Throws"/> is set.</summary>
    public sealed class AddressValidator : AbstractValidator<Address>
    {
        public AddressValidator() =>
            RuleFor(m => m.Street).MustAsync(async (_, _) =>
            {
                await Task.Yield();
                if (Throws)
                {
                    throw new HttpRequestException(ServiceDown);
                }

                return true;
            });

        public bool Throws { get; set; }
    }

    [Fact]
    public async Task A_submit_whose_rule_throws_is_blocked_and_reports_the_fault()
    {
        var model = new Address();
        var editContext = new EditContext(model);
        var validator = new AddressValidator { Throws = true };
        var logger = new CapturingLogger();
        using var engine = new FormidableEngine<Address>(
            model, editContext, new FluentValidationModelValidator<Address>(validator), new ReflectionModelIntrospector(),
            new FormidableOptions(), _time, null, logger, null);
        var faults = new List<Exception>();
        engine.ValidationFaulted += (_, e) => faults.Add(e.Exception);

        var outcome = await engine.ValidateForSubmitAsync();

        Assert.False(outcome.CanProceed);
        var fault = Assert.IsType<HttpRequestException>(outcome.Fault);
        Assert.Equal(ServiceDown, fault.Message);
        Assert.Same(fault, Assert.Single(faults));
        Assert.Equal([engine.Options.ModelLevelDisplayName], outcome.VisibleErrorSummary);
        Assert.Contains(engine.GetVisibleIssues(), issue => issue.Issue.Message == engine.Options.ValidationFaultMessage);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(fault, Assert.Single(logger.Exceptions));
        Assert.False(engine.IsValidating);
        Assert.False(engine.HasSubmitted);
    }

    [Fact]
    public async Task A_load_whose_rule_throws_shows_the_fault_and_returns()
    {
        var model = new Address();
        var editContext = new EditContext(model);
        var validator = new AddressValidator { Throws = true };
        var logger = new CapturingLogger();
        using var engine = new FormidableEngine<Address>(
            model, editContext, new FluentValidationModelValidator<Address>(validator), new ReflectionModelIntrospector(),
            new FormidableOptions(), _time, null, logger, null);
        var faults = new List<Exception>();
        engine.ValidationFaulted += (_, e) => faults.Add(e.Exception);

        await engine.DiscloseLoadedValuesAsync();

        var fault = Assert.IsType<HttpRequestException>(Assert.Single(faults));
        Assert.Equal(ServiceDown, fault.Message);
        Assert.Contains(engine.GetVisibleIssues(), issue => issue.Issue.Message == engine.Options.ValidationFaultMessage);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
        Assert.Same(fault, Assert.Single(logger.Exceptions));
        Assert.False(engine.IsValidating);
        Assert.False(engine.GetFieldState(new FieldIdentifier(model, nameof(Address.Street))).IsTouched);
    }

    [Fact]
    public async Task The_form_s_own_submit_blocks_shows_the_fault_and_moves_focus_to_the_form()
    {
        var model = new Address();
        var validator = new AddressValidator { Throws = true };
        FormidableInvalidSubmitContext? invalid = null;
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Address>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<Address>)new FluentValidationModelValidator<Address>(validator));
            builder.AddComponentParameter(3, "OnInvalidSubmit", EventCallback.Factory.Create<FormidableInvalidSubmitContext>(this, c => invalid = c));
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                inner.CloseComponent();
                inner.AddMarkupContent(1, "<button type=\"submit\">Save</button>");
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<Address>>().Instance;
        await cut.InvokeAsync(() => { });
        var focusBefore = _focus.Requests.Count;

        var thrown = await Record.ExceptionAsync(() => cut.InvokeAsync(() => cut.Find("form").SubmitAsync()));
        await cut.InvokeAsync(() => { });

        Assert.Null(thrown);
        Assert.False(Renderer.UnhandledException.IsCompleted);
        Assert.NotNull(invalid);
        Assert.IsType<HttpRequestException>(invalid.Outcome.Fault);
        Assert.Contains(form.Engine!.Options.ValidationFaultMessage, cut.Markup, StringComparison.Ordinal);
        Assert.Equal(new FieldIdentifier(model, string.Empty), Assert.Single(_focus.Requests.Skip(focusBefore)));
        Assert.False(form.IsSubmitting);
        Assert.Contains(_logs.Exceptions, exception => exception is HttpRequestException);

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task The_attached_validator_s_submit_is_blocked_and_reports_the_fault()
    {
        var model = new Address();
        var validator = new AddressValidator { Throws = true };
        var cut = Render(builder =>
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, nameof(EditForm.Model), model);
            builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<Address>>(0);
                inner.AddComponentParameter(1, "Validator", (IModelValidator<Address>)new FluentValidationModelValidator<Address>(validator));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var attached = cut.FindComponent<FormidableValidator<Address>>().Instance;
        await cut.InvokeAsync(() => { });
        var focusBefore = _focus.Requests.Count;

        var outcome = await cut.InvokeAsync(() => attached.ValidateForSubmitAsync());
        await cut.InvokeAsync(() => { });

        Assert.False(outcome.CanProceed);
        Assert.IsType<HttpRequestException>(outcome.Fault);
        Assert.Contains(attached.Engine!.GetVisibleIssues(), issue => issue.Issue.Message == attached.Engine.Options.ValidationFaultMessage);
        Assert.Equal(new FieldIdentifier(model, string.Empty), Assert.Single(_focus.Requests.Skip(focusBefore)));

        await Services.DisposeAsync();
    }

    // Formidable's own refusals of a misbuilt rule-level validator fail every time, so a submit
    // hands them to its caller rather than showing a fault message. Each row builds one a
    // different way: a decorator that claims the capability over a validator without it (core
    // refuses with NotSupportedException), one whose groups drop the rules (the engine refuses
    // with InvalidOperationException), and one that hands over another validator's rule
    // identities (core refuses with ArgumentException). The empty-profile refusals are pinned
    // by EmptyProfileFormTests.
    [Theory]
    [InlineData("claims-capability")]
    [InlineData("drops-the-rules")]
    [InlineData("foreign-identities")]
    public async Task A_refusal_of_a_misbuilt_rule_level_validator_still_throws_from_a_submit(string shape)
    {
        var model = new Address();
        var editContext = new EditContext(model);
        IModelValidator<Address> validator = shape switch
        {
            "claims-capability" => new MisbuiltValidator(new FluentValidationModelValidator<Address>(new PreValidatingAddressValidator())) { ClaimsCapability = true },
            "drops-the-rules" => new MisbuiltValidator(new FluentValidationModelValidator<Address>(new AddressValidator())) { DropsTheRules = true },
            _ => new MisbuiltValidator(new FluentValidationModelValidator<Address>(new AddressValidator()))
            {
                ForeignIdentities = new FluentValidationModelValidator<Address>(new AddressValidator()),
            },
        };
        var logger = new CapturingLogger();
        using var engine = new FormidableEngine<Address>(
            model, editContext, validator, new ReflectionModelIntrospector(),
            new FormidableOptions(), _time, null, logger, null);
        var faults = 0;
        engine.ValidationFaulted += (_, _) => faults++;

        var thrown = await Record.ExceptionAsync(() => engine.ValidateForSubmitAsync());

        Assert.IsType(
            shape switch
            {
                "claims-capability" => typeof(NotSupportedException),
                "drops-the-rules" => typeof(InvalidOperationException),
                _ => typeof(ArgumentException),
            },
            thrown);
        Assert.Equal(0, faults);
        Assert.Empty(logger.Entries);
        Assert.DoesNotContain(engine.GetVisibleIssues(), issue => issue.Issue.Message == engine.Options.ValidationFaultMessage);
    }

    /// <summary>Street must not be empty, and the class overrides <c>PreValidate</c>, so it cannot be validated rule by rule.</summary>
    public sealed class PreValidatingAddressValidator : AbstractValidator<Address>
    {
        public PreValidatingAddressValidator() => RuleFor(m => m.Street).NotEmpty();

        protected override bool PreValidate(ValidationContext<Address> context, FluentValidation.Results.ValidationResult result) => true;
    }

    /// <summary>A decorator that misbuilds the rule-level seam in the way each switch names.</summary>
    public sealed class MisbuiltValidator(IModelValidator<Address> inner) : DelegatingModelValidator<Address>(inner)
    {
        public bool ClaimsCapability { get; init; }

        public bool DropsTheRules { get; init; }

        public IRuleLevelValidator<Address>? ForeignIdentities { get; init; }

        public override bool CanValidateByRule => ClaimsCapability || base.CanValidateByRule;

        public override IReadOnlyList<RuleIdentity> SelectRules(ValidationProfile profile) =>
            ForeignIdentities is { } foreign ? foreign.SelectRules(profile) : base.SelectRules(profile);

        public override IReadOnlyList<IReadOnlyList<RuleIdentity>> GroupBySelectionClass(IReadOnlyList<RuleIdentity> rules) =>
            DropsTheRules ? [] : base.GroupBySelectionClass(rules);
    }

    // A ValidationFaulted handler that throws during a submit makes the submit throw that
    // exception, as any handler a call raises does. The warning is written before the event, so it
    // stands although the handler threw. Mutation that must break it: log after the event, and the
    // warning is missing.
    [Fact]
    public async Task A_fault_handler_that_throws_reaches_the_submit_s_caller_after_the_warning()
    {
        var model = new Address();
        var editContext = new EditContext(model);
        var logger = new CapturingLogger();
        using var engine = new FormidableEngine<Address>(
            model, editContext, new FluentValidationModelValidator<Address>(new AddressValidator { Throws = true }),
            new ReflectionModelIntrospector(), new FormidableOptions(), _time, null, logger, null);
        engine.ValidationFaulted += (_, _) => throw new InvalidOperationException("handler");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ValidateForSubmitAsync());

        Assert.Equal("handler", thrown.Message);
        Assert.IsType<HttpRequestException>(Assert.Single(logger.Exceptions));
        Assert.False(engine.IsValidating);
    }

    // A submit a newer submit has displaced, whose rule then fails, files no fault message, so it
    // reports as any displaced submit does: blocked, with nothing of its own and no Fault. The event
    // and the warning still record the exception. Mutation that must break it: report Fault whether
    // or not the message was filed, and the displaced outcome carries it.
    [Fact]
    public async Task A_displaced_submit_whose_rule_then_fails_reports_no_fault()
    {
        var model = new Address();
        var editContext = new EditContext(model);
        var validator = new DisplacedThenFailingValidator();
        var logger = new CapturingLogger();
        using var engine = new FormidableEngine<Address>(
            model, editContext, new FluentValidationModelValidator<Address>(validator), new ReflectionModelIntrospector(),
            new FormidableOptions(), _time, null, logger, null);
        var faults = new List<Exception>();
        engine.ValidationFaulted += (_, e) => faults.Add(e.Exception);

        var first = engine.ValidateForSubmitAsync();
        Assert.True(await UntilAsync(() => validator.Runs == 1));
        var second = await engine.ValidateForSubmitAsync();
        validator.Gate.SetResult();
        var displaced = await first;

        Assert.True(second.CanProceed);
        Assert.False(displaced.CanProceed);
        Assert.Null(displaced.Fault);
        Assert.Empty(displaced.VisibleErrorSummary);
        Assert.IsType<HttpRequestException>(Assert.Single(faults));
        Assert.Single(logger.Entries);
        Assert.DoesNotContain(engine.GetVisibleIssues(), issue => issue.Issue.Message == engine.Options.ValidationFaultMessage);
    }

    /// <summary>Street's async rule: the first run waits for <see cref="Gate"/>, ignoring its token, then throws; every later run passes at once.</summary>
    public sealed class DisplacedThenFailingValidator : AbstractValidator<Address>
    {
        private int _runs;

        public DisplacedThenFailingValidator() =>
            RuleFor(m => m.Street).MustAsync(async (_, _) =>
            {
                if (Interlocked.Increment(ref _runs) == 1)
                {
                    await Gate.Task;
                    throw new HttpRequestException(ServiceDown);
                }

                return true;
            });

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Runs => Volatile.Read(ref _runs);
    }

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    // The control: a submit whose rule answers normally carries no fault.
    [Fact]
    public async Task A_submit_whose_rule_answers_carries_no_fault()
    {
        var model = new Address();
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<Address>(
            model, editContext, new FluentValidationModelValidator<Address>(new AddressValidator()), new ReflectionModelIntrospector(),
            new FormidableOptions(), _time, null, new CapturingLogger(), null);

        var outcome = await engine.ValidateForSubmitAsync();

        Assert.True(outcome.CanProceed);
        Assert.Null(outcome.Fault);
    }
}
