using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A form whose submit profile or live profile leaves out the default rules and names a ruleset
/// its validator never declares. A plain validator selects no rule under it and a
/// <see cref="ProfiledValidator{T}"/> never registered the name, so both throw wherever the form
/// validates under it: a live check or the whole-form re-check that runs it shows the fault
/// message, and a submit or a load of values that runs it throws to whoever called it.
/// </summary>
// Mutation that must break it: drop the configuration-error mark from either refusal, and the
// submit and load rows of that validator kind report a fault instead of throwing.
public class EmptyProfileFormTests : BunitContext
{
    private readonly FakeTimeProvider _clock = new();

    public EmptyProfileFormTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddFormidableBlazor();
    }

    public enum ValidatorKind
    {
        // An AbstractValidator<T> with a default rule and an "Approve" ruleset.
        Plain,

        // A ProfiledValidator<T> with the same rules, which checks ruleset names itself.
        Profiled,
    }

    // The whole-form check a form runs once its first render settles throws first, so the fault
    // message shows before any edit. The first edit's own check throws again and raises the event,
    // and the validity check behind TrackFormValidity throws too, so IsFormValid never reads true.
    // The Profiled row is the control: it throws the same way before the plain check existed.
    // Mutation: make the plain check return instead of throwing. The Plain row then shows no fault
    // message, raises nothing and reads IsFormValid true.
    [Theory]
    [InlineData(ValidatorKind.Plain)]
    [InlineData(ValidatorKind.Profiled)]
    public async Task A_form_under_an_empty_profile_shows_the_fault_message_on_the_first_edit(ValidatorKind kind)
    {
        var options = TypoOptions();
        var cut = Render(Form(new PhantomForm(), kind, options, boundary: false, onValid: () => { }, onInvalid: () => { }));
        var engine = cut.FindComponent<FormidableForm<PhantomForm>>().Instance.Engine!;
        await cut.InvokeAsync(() => _clock.Advance(options.RefreshDebounce + TimeSpan.FromMilliseconds(1)));
        cut.WaitForState(() => !engine.IsValidating);
        Assert.Contains(options.ValidationFaultMessage, cut.Markup, StringComparison.Ordinal);

        var faults = new List<Exception>();
        engine.ValidationFaulted += (_, e) => faults.Add(e.Exception);
        await cut.InvokeAsync(() => cut.Find("input").Change(""));
        cut.WaitForState(() => !engine.IsValidating);
        await cut.InvokeAsync(() => { });

        Assert.Contains(options.ValidationFaultMessage, cut.Markup, StringComparison.Ordinal);
        Assert.Contains(faults, fault => fault is InvalidOperationException);
        Assert.False(engine.IsFormValid);
        Assert.DoesNotContain(NameRequired, cut.Markup, StringComparison.Ordinal);
    }

    // A page that calls SubmitAsync itself is its caller, and gets the exception. The rendered
    // form's own submit has Blazor's event dispatch as its caller, which hands the exception to the
    // ErrorBoundary around the form. Neither callback runs either way.
    // Mutation: make the plain check return instead of throwing. The Plain rows then submit as
    // valid, run OnValidSubmit and throw nothing.
    [Theory]
    [InlineData(ValidatorKind.Plain, false)]
    [InlineData(ValidatorKind.Plain, true)]
    [InlineData(ValidatorKind.Profiled, false)]
    [InlineData(ValidatorKind.Profiled, true)]
    public async Task A_submit_under_an_empty_profile_throws_to_its_caller(ValidatorKind kind, bool renderedSubmitInBoundary)
    {
        var validSubmits = 0;
        var invalidSubmits = 0;
        var cut = Render(Form(
            new PhantomForm(), kind, TypoOptions(), renderedSubmitInBoundary,
            onValid: () => validSubmits++, onInvalid: () => invalidSubmits++));

        if (renderedSubmitInBoundary)
        {
            await cut.InvokeAsync(() => cut.Find("form").SubmitAsync());
            await cut.InvokeAsync(() => { });

            Assert.Equal(nameof(InvalidOperationException), cut.Find("p.boundary-error").TextContent);
            Assert.False(Renderer.UnhandledException.IsCompleted);
        }
        else
        {
            var form = cut.FindComponent<FormidableForm<PhantomForm>>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => cut.InvokeAsync(() => form.Instance.SubmitAsync()));
        }

        Assert.Equal(0, validSubmits);
        Assert.Equal(0, invalidSubmits);
    }

    // A load of values is awaited like a submit, so its exception goes to the code that called it
    // and the form files no fault message for it. No clock moves here, so the whole-form re-check
    // after the first render never runs and cannot show the message either.
    // Mutation: report a load's fault as the live and re-check faults are reported. The load then
    // throws nothing and the fault message shows. Mutation: make the plain check return instead of
    // throwing. The Plain row then loads without a throw.
    [Theory]
    [InlineData(ValidatorKind.Plain)]
    [InlineData(ValidatorKind.Profiled)]
    public async Task A_load_of_values_under_an_empty_profile_throws_to_its_caller(ValidatorKind kind)
    {
        var options = TypoOptions();
        var cut = Render(Form(new PhantomForm(), kind, options, boundary: false, onValid: () => { }, onInvalid: () => { }));
        var form = cut.FindComponent<FormidableForm<PhantomForm>>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => cut.InvokeAsync(() => form.Instance.DiscloseLoadedValuesAsync()));
        await cut.InvokeAsync(() => { });

        Assert.DoesNotContain(options.ValidationFaultMessage, cut.Markup, StringComparison.Ordinal);
    }

    // The typo sits in LiveProfile alone, and SubmitProfile is the built-in Submit. The whole-form
    // re-check after the first render runs Submit, so nothing shows before an edit; the first
    // edit's live check runs the typo and shows the fault message; a submit runs Submit as usual
    // and blocks on the cleared name. Only the plain validator fits: a ProfiledValidator that never
    // registered "Submit" refuses the built-in Submit profile itself.
    // Mutation: make the plain check return instead of throwing. No fault message then shows after
    // the edit.
    [Fact]
    public async Task An_empty_live_profile_shows_the_fault_message_on_the_first_edit_and_leaves_submits_alone()
    {
        var options = new FormidableOptions
        {
            LiveProfile = ValidationProfile.Named("Aprove", includeDefaultRules: false, "Aprove"),
            TrackFormValidity = true,
        };
        var validSubmits = 0;
        var invalidSubmits = 0;
        var cut = Render(Form(
            new PhantomForm(), ValidatorKind.Plain, options, boundary: false,
            onValid: () => validSubmits++, onInvalid: () => invalidSubmits++));
        var form = cut.FindComponent<FormidableForm<PhantomForm>>();
        var engine = form.Instance.Engine!;
        await cut.InvokeAsync(() => _clock.Advance(options.RefreshDebounce + TimeSpan.FromMilliseconds(1)));
        cut.WaitForState(() => !engine.IsValidating);
        Assert.DoesNotContain(options.ValidationFaultMessage, cut.Markup, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Find("input").Change(""));
        cut.WaitForState(() => !engine.IsValidating);
        await cut.InvokeAsync(() => { });
        Assert.Contains(options.ValidationFaultMessage, cut.Markup, StringComparison.Ordinal);

        var outcome = await cut.InvokeAsync(() => form.Instance.SubmitAsync());
        await cut.InvokeAsync(() => { });

        Assert.False(outcome.CanProceed);
        Assert.Equal(0, validSubmits);
        Assert.Equal(1, invalidSubmits);
        Assert.Contains(NameRequired, cut.Markup, StringComparison.Ordinal);
    }

    private const string NameRequired = "Name is required";

    // "Aprove" is the typo: both validators declare "Approve".
    private static FormidableOptions TypoOptions() => new()
    {
        SubmitProfile = ValidationProfile.Named("Aprove", includeDefaultRules: false, "Aprove"),
        TrackFormValidity = true,
    };

    private RenderFragment Form(
        PhantomForm model, ValidatorKind kind, FormidableOptions options, bool boundary, Action onValid, Action onInvalid)
    {
        AbstractValidator<PhantomForm> validator = kind == ValidatorKind.Plain
            ? new PlainPhantomFormValidator()
            : new ProfiledPhantomFormValidator();

        RenderFragment form = builder =>
        {
            builder.OpenComponent<FormidableForm<PhantomForm>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<PhantomForm>)new FluentValidationModelValidator<PhantomForm>(validator));
            builder.AddComponentParameter(3, "Options", options);
            builder.AddComponentParameter(4, "OnValidSubmit", EventCallback.Factory.Create<SubmitOutcome>(this, _ => onValid()));
            builder.AddComponentParameter(5, "OnInvalidSubmit", EventCallback.Factory.Create<FormidableInvalidSubmitContext>(this, _ => onInvalid()));
            builder.AddComponentParameter(6, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                inner.CloseComponent();
                inner.OpenComponent<FormidableInputText>(1);
                inner.AddComponentParameter(2, "Value", model.Name);
                inner.AddComponentParameter(3, "ValueChanged", EventCallback.Factory.Create<string?>(this, value => model.Name = value));
                inner.AddComponentParameter(4, "ValueExpression", (Expression<Func<string?>>)(() => model.Name));
                inner.CloseComponent();
                inner.AddMarkupContent(5, "<button type=\"submit\">Save</button>");
            }));
            builder.CloseComponent();
        };

        if (!boundary)
        {
            return form;
        }

        return builder =>
        {
            builder.OpenComponent<ErrorBoundary>(0);
            builder.AddComponentParameter(1, "ChildContent", form);
            builder.AddComponentParameter(2, "ErrorContent", (RenderFragment<Exception>)(exception => content =>
            {
                content.OpenElement(0, "p");
                content.AddAttribute(1, "class", "boundary-error");
                content.AddContent(2, exception.GetType().Name);
                content.CloseElement();
            }));
            builder.CloseComponent();
        };
    }

    public sealed class PhantomForm
    {
        public string? Name { get; set; } = "Ada";
    }

    private sealed class PlainPhantomFormValidator : AbstractValidator<PhantomForm>
    {
        public PlainPhantomFormValidator()
        {
            RuleFor(m => m.Name).NotEmpty().WithMessage(NameRequired);
            RuleSet("Approve", () => RuleFor(m => m.Name).MaximumLength(3));
        }
    }

    private sealed class ProfiledPhantomFormValidator : ProfiledValidator<PhantomForm>
    {
        protected override void ConfigureCommonRules() => RuleFor(m => m.Name).NotEmpty().WithMessage(NameRequired);

        protected override void ConfigureProfiles() => Profile("Approve", () => RuleFor(m => m.Name).MaximumLength(3));
    }
}
