using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A page that reads field state inside its <see cref="IFormidableEngine.StateChanged"/> handler
/// (the live-indicator pattern) reads at the first-touch publish of an edit, before the edit has
/// armed anything. A sibling wearing green on the held vouch keeps it: whether a re-answer is on
/// its way is asked again at the next read, once the arm exists, rather than the declined answer
/// standing until a landing. Engine-level on a fake clock, under TrackFormValidity and a live
/// window that never closes, so the only re-answer is the validity check the edit arms after its
/// first-touch publish.
/// </summary>
public class HeldGreenInlineReadTests
{
    private static readonly TimeSpan Refresh = new FormidableOptions().RefreshDebounce;

    // Mutation: drop the promise re-check for a declined serve in EnsureSubmitCoverageCurrent. The
    // handler's read then caches the declined answer, and the name reads false until the check
    // lands.
    [Fact]
    public void A_green_field_keeps_green_when_a_handler_reads_it_at_another_fields_first_touch()
    {
        using var form = GreenNameForm.Build();
        form.Engine.StateChanged += (_, _) => _ = form.Engine.GetFieldState(form.Name).WouldPassSubmit;

        form.Order.Description = "still ok";
        form.EditContext.NotifyFieldChanged(form.Description);

        Assert.True(form.Engine.GetFieldState(form.Name).WouldPassSubmit);
    }

    // Control: with nothing reading inside the publish, the first read after the edit comes after
    // the arm, and the held answer is served on the check's promise.
    [Fact]
    public void A_green_field_keeps_green_at_another_fields_first_touch_with_no_reader()
    {
        using var form = GreenNameForm.Build();

        form.Order.Description = "still ok";
        form.EditContext.NotifyFieldChanged(form.Description);

        Assert.True(form.Engine.GetFieldState(form.Name).WouldPassSubmit);
    }

    // A decline with nothing on its way is asked again at each read, so a submit that begins
    // afterwards re-serves the held answer for as long as its check runs. Tracking is off at the
    // edit (options are read at each use) under the window that never closes, so the edit starts
    // and arms nothing and the vouch is withheld, as documented; the submit's own check is then
    // the re-answer on its way, and the name is green while its gated rule waits.
    [Fact]
    public async Task A_submit_beginning_after_a_decline_re_serves_the_held_vouch_while_it_runs()
    {
        var validator = new GatedValidator();
        validator.Gate.SetResult();
        using var form = GreenNameForm.Build(validator);
        form.Options.TrackFormValidity = false;
        validator.Gate = new TaskCompletionSource();

        form.Order.Description = "still ok";
        form.EditContext.NotifyFieldChanged(form.Description);
        Assert.False(form.Engine.GetFieldState(form.Name).WouldPassSubmit, "precondition: nothing is on its way, so the vouch is withheld");

        var submit = form.Engine.ValidateForSubmitAsync();
        for (var i = 0; i < 50 && !form.Engine.IsValidating; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(form.Engine.IsValidating, "the gated check is not running");
        Assert.True(form.Engine.GetFieldState(form.Name).WouldPassSubmit);

        validator.Gate.SetResult();
        await submit;
    }

    // The handler's promise never outranks the bound. A pass hung past HeldVouchBound has stopped
    // vouching for every other field, and a new edit's handler, which will arm a re-check the hung
    // pass defers, must not put the green back for the one publish it runs in. Mutation: test the
    // promise before the bound in ReAnswerOnItsWay; the touch publish then vouches and the first
    // read after the handler declines.
    [Fact]
    public async Task A_new_edits_touch_publish_does_not_vouch_past_the_bound_of_a_hung_pass()
    {
        var validator = new GatedValidator();
        validator.Gate.SetResult();
        using var form = GreenNameForm.Build(validator);
        validator.Gate = new TaskCompletionSource();
        var location = new FieldIdentifier(form.Order, nameof(EngineOrder.Location));

        // An edit after the hold, so the held answer is served across it on a re-answer's promise.
        form.Order.Description = "still ok";
        form.EditContext.NotifyFieldChanged(form.Description);
        Assert.True(form.Engine.GetFieldState(form.Name).WouldPassSubmit, "precondition: the name is not green across the edit");

        // A submit that hangs on the gated rule, then the bound passes.
        var submit = form.Engine.ValidateForSubmitAsync();
        for (var i = 0; i < 50 && !form.Engine.IsValidating; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(form.Engine.IsValidating, "the gated check is not running");
        form.Time.Advance(SubmitCoverageTracker.HeldVouchBound);
        Assert.False(form.Engine.GetFieldState(form.Name).WouldPassSubmit, "precondition: the hung pass still vouches past the bound");
        var reads = new List<bool>();
        form.Engine.StateChanged += (_, _) => reads.Add(form.Engine.GetFieldState(form.Name).WouldPassSubmit);

        form.EditContext.NotifyFieldChanged(location);

        Assert.NotEmpty(reads);
        Assert.All(reads, read => Assert.False(read, "the touch publish vouched past the bound"));
        Assert.False(form.Engine.GetFieldState(form.Name).WouldPassSubmit);

        validator.Gate.SetResult();
        await submit;
    }

    /// <summary>An engine whose customer name wears green on a landed validity check, and whose description has never been touched.</summary>
    private sealed class GreenNameForm : IDisposable
    {
        public required FormidableEngine<EngineOrder> Engine { get; init; }
        public required EditContext EditContext { get; init; }
        public required FormidableOptions Options { get; init; }
        public required EngineOrder Order { get; init; }
        public required FieldIdentifier Name { get; init; }
        public required FieldIdentifier Description { get; init; }
        public required FakeTimeProvider Time { get; init; }

        public static GreenNameForm Build(IValidator<EngineOrder>? validator = null)
        {
            var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer { Name = "Bo" } };
            var editContext = new EditContext(order);
            var time = new FakeTimeProvider();
            var options = new FormidableOptions { TrackFormValidity = true, LiveDebounce = Timeout.InfiniteTimeSpan };
            var engine = new FormidableEngine<EngineOrder>(
                order,
                editContext,
                new FluentValidationModelValidator<EngineOrder>(validator ?? new DeclarationOrderValidator()),
                new ReflectionModelIntrospector(),
                options,
                time);
            var name = new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name));

            // The name's own edit arms the validity check; its landing files the answer the name
            // wears green on, held at this edit stamp.
            order.Customer!.Name = "Bea";
            editContext.NotifyFieldChanged(name);
            time.Advance(Refresh);
            Assert.True(engine.GetFieldState(name).WouldPassSubmit, "setup: the name is not green after the check lands");

            return new GreenNameForm
            {
                Engine = engine,
                EditContext = editContext,
                Options = options,
                Order = order,
                Name = name,
                Description = new FieldIdentifier(order, nameof(EngineOrder.Description)),
                Time = time,
            };
        }

        public void Dispose() => Engine.Dispose();
    }

    /// <summary>A synchronous description rule and a name rule that waits on <see cref="Gate"/> and then passes.</summary>
    private sealed class GatedValidator : DraftSubmitValidator<EngineOrder>
    {
        public TaskCompletionSource Gate { get; set; } = new();

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(x => x.Description).NotEmpty().WithMessage("Description is required");

            RuleFor(x => x.Customer!.Name)
                .MustAsync(async (_, ct) =>
                {
                    await Gate.Task.WaitAsync(ct);
                    return true;
                })
                .WithMessage("Customer name failed its async check")
                .When(x => x.Customer is not null);
        }
    }
}
