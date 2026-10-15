using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A rule that cancels its own work, with neither the caller nor the form cancelling it (an
/// <c>HttpClient</c> timeout arrives as a <see cref="TaskCanceledException"/> this way), is the
/// validator's fault, as any other exception it throws is. A live check, a submit and a load of
/// values show the form's fault message and raise <see cref="IFormidableEngine.ValidationFaulted"/>,
/// and a submit is blocked with the exception as its outcome's fault; the validity check raises
/// the event; and the form's own submit event ends without a throw. A cancellation the caller or a
/// newer check made is still no fault.
/// </summary>
// Mutation that must break it: let the pass's first cancellation catch take any cancellation the
// caller did not make, as if superseded, and the live, submit, form-event and load rows find
// nothing reported. Separately: let the validity check's cancellation catch take any cancellation,
// and the validity row raises no event. And the control: let only a submit or a load stand down on
// a cancellation its own pass's token carries, so a live check's supersession takes the fault
// route, and the live control row raises the event.
public class RuleCancellationFaultTests : BunitContext
{
    private const string TimedOut = "The username service timed out.";

    private readonly FakeTimeProvider _clock = new();

    public RuleCancellationFaultTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddFormidableBlazor();
    }

    public sealed class Account
    {
        public string? Name { get; set; } = "Ann";
    }

    /// <summary>One async rule on Name that, once it has awaited, throws a <see cref="TaskCanceledException"/> of its own while <see cref="TimesOut"/> is set; its token is never cancelled by then.</summary>
    public sealed class TimingOutValidator : AbstractValidator<Account>
    {
        public TimingOutValidator() =>
            RuleFor(m => m.Name).MustAsync(async (_, ct) =>
            {
                if (Gate is { } gate)
                {
                    await gate.Task;
                }

                await Task.Yield();
                if (TimesOut)
                {
                    TokenCancelledAtThrow = ct.IsCancellationRequested;
                    throw new TaskCanceledException(TimedOut);
                }

                return true;
            });

        public bool TimesOut { get; set; }

        /// <summary>Held until the test releases it, when set: the rule waits on it before anything else.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public bool? TokenCancelledAtThrow { get; private set; }
    }

    [Fact]
    public async Task A_live_check_whose_rule_cancels_itself_shows_the_fault_message_and_raises_the_event()
    {
        var validator = new TimingOutValidator();
        var model = new Account();
        var cut = RenderForm(model, validator, new FormidableOptions());
        var engine = cut.Instance.Engine!;
        var faults = new List<Exception>();
        engine.ValidationFaulted += (_, e) => faults.Add(e.Exception);

        validator.TimesOut = true;
        await cut.InvokeAsync(() => cut.Find("input").Change("Bea"));
        cut.WaitForState(() => faults.Count > 0 && !engine.IsValidating);
        await cut.InvokeAsync(() => { });

        Assert.False(validator.TokenCancelledAtThrow);
        Assert.Equal(TimedOut, Assert.IsType<TaskCanceledException>(Assert.Single(faults)).Message);
        Assert.Contains(engine.Options.ValidationFaultMessage, cut.Markup, StringComparison.Ordinal);

        await Services.DisposeAsync();
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("load")]
    public async Task A_submit_or_load_whose_rule_cancels_itself_faults_the_form(string route)
    {
        var validator = new TimingOutValidator();
        var model = new Account();
        var cut = RenderForm(model, validator, new FormidableOptions());
        var engine = cut.Instance.Engine!;
        var faults = new List<Exception>();
        engine.ValidationFaulted += (_, e) => faults.Add(e.Exception);

        validator.TimesOut = true;
        SubmitOutcome? outcome = null;
        await cut.InvokeAsync(async () =>
        {
            if (route == "submit")
            {
                outcome = await engine.ValidateForSubmitAsync();
            }
            else
            {
                await engine.DiscloseLoadedValuesAsync();
            }
        });
        await cut.InvokeAsync(() => { });

        Assert.False(validator.TokenCancelledAtThrow);
        var fault = Assert.IsType<TaskCanceledException>(Assert.Single(faults));
        Assert.Equal(TimedOut, fault.Message);
        Assert.Contains(engine.Options.ValidationFaultMessage, cut.Markup, StringComparison.Ordinal);
        if (outcome is not null)
        {
            Assert.False(outcome.CanProceed);
            Assert.Same(fault, outcome.Fault);
        }

        await Services.DisposeAsync();
    }

    // The form's own submit event ends blocked rather than with a throw: the fault message shows,
    // and nothing reaches the renderer's unhandled path.
    [Fact]
    public async Task The_form_s_own_submit_blocks_on_the_rule_s_cancellation()
    {
        var validator = new TimingOutValidator();
        var model = new Account();
        var cut = RenderForm(model, validator, new FormidableOptions());

        validator.TimesOut = true;
        var thrown = await Record.ExceptionAsync(() => cut.InvokeAsync(() => cut.Find("form").SubmitAsync()));
        await cut.InvokeAsync(() => { });

        Assert.Null(thrown);
        Assert.False(Renderer.UnhandledException.IsCompleted);
        Assert.Contains(cut.Instance.Engine!.Options.ValidationFaultMessage, cut.Markup, StringComparison.Ordinal);
        Assert.False(cut.Instance.IsSubmitting);

        await Services.DisposeAsync();
    }

    // The control: a caller that cancels its own token still gets a cancellation, and a rule that
    // honours it is no fault.
    [Fact]
    public async Task A_caller_s_cancellation_still_throws_a_cancellation()
    {
        var model = new Account();
        var editContext = new EditContext(model);
        var gate = new TaskCompletionSource();
        var validator = new InlineValidator<Account>();
        validator.RuleFor(m => m.Name).MustAsync(async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return true;
        });
        using var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator), new ReflectionModelIntrospector(),
            new FormidableOptions(), _clock);
        using var cancel = new CancellationTokenSource();

        var submit = engine.ValidateForSubmitAsync(cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
    }

    // The live control: a check that a newer edit's check overtakes, with a rule that honours its
    // token, raises no event and shows no fault message.
    [Fact]
    public async Task A_live_check_a_newer_edit_overtakes_raises_no_fault()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var cancelledSeen = 0;
        var validator = new InlineValidator<Account>();
        validator.RuleFor(m => m.Name).MustAsync(async (_, ct) =>
        {
            Interlocked.Increment(ref started);
            try
            {
                await gate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelledSeen);
                throw;
            }

            return true;
        });
        var model = new Account();
        var editContext = new EditContext(model);
        var faults = 0;
        using var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator), new ReflectionModelIntrospector(),
            new FormidableOptions(), _clock);
        engine.ValidationFaulted += (_, _) => Interlocked.Increment(ref faults);
        var name = new FieldIdentifier(model, nameof(Account.Name));

        model.Name = "Bea";
        editContext.NotifyFieldChanged(name);
        Assert.True(await UntilAsync(() => Volatile.Read(ref started) == 1));

        model.Name = "Cy";
        editContext.NotifyFieldChanged(name);
        Assert.True(await UntilAsync(() => Volatile.Read(ref cancelledSeen) == 1));

        gate.SetResult();
        Assert.True(await UntilAsync(() => !engine.IsValidating));
        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref faults));
        Assert.DoesNotContain(engine.GetVisibleIssues(), issue => issue.Issue.Message == engine.Options.ValidationFaultMessage);
    }

    // The TrackFormValidity check the engine starts as it is built runs the same rule. It shows
    // nothing, as it never does, and raises the event for the rule's own cancellation. The rule
    // waits until the test has subscribed.
    [Fact]
    public async Task A_validity_check_whose_rule_cancels_itself_raises_the_event()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validator = new TimingOutValidator { TimesOut = true, Gate = gate };
        var model = new Account();
        var editContext = new EditContext(model);
        var faults = new List<Exception>();
        using var engine = new FormidableEngine<Account>(
            model, editContext, new FluentValidationModelValidator<Account>(validator), new ReflectionModelIntrospector(),
            new FormidableOptions { TrackFormValidity = true }, _clock);
        engine.ValidationFaulted += (_, e) =>
        {
            lock (faults)
            {
                faults.Add(e.Exception);
            }
        };

        int Count()
        {
            lock (faults)
            {
                return faults.Count;
            }
        }

        gate.SetResult();

        for (var i = 0; i < 300 && Count() == 0; i++)
        {
            await Task.Delay(10);
        }

        lock (faults)
        {
            Assert.Equal(TimedOut, Assert.IsType<TaskCanceledException>(Assert.Single(faults)).Message);
        }

        Assert.Empty(engine.GetVisibleIssues());
    }

    private IRenderedComponent<FormidableForm<Account>> RenderForm(Account model, IValidator<Account> validator, FormidableOptions options)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Account>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "Validator", (IModelValidator<Account>)new FluentValidationModelValidator<Account>(validator));
            builder.AddComponentParameter(3, "Options", options);
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner => BuildContent(inner, model)));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<Account>>();
    }

    private void BuildContent(RenderTreeBuilder inner, Account model)
    {
        inner.OpenComponent<FormidableSummary>(0);
        inner.CloseComponent();
        inner.OpenComponent<FormidableInputText>(1);
        inner.AddComponentParameter(2, "Value", model.Name);
        inner.AddComponentParameter(3, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => model.Name = v));
        inner.AddComponentParameter(4, "ValueExpression", (System.Linq.Expressions.Expression<Func<string?>>)(() => model.Name));
        inner.CloseComponent();
        inner.AddMarkupContent(5, "<button type=\"submit\">Save</button>");
    }

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        return condition();
    }
}
