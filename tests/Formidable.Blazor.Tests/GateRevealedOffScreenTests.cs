using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// The form-level message (the gate) on a blocked submit whose shown errors sit on fields that are
/// off screen now. A field a submit has revealed stays revealed when it leaves the page, so the
/// summary keeps listing its errors and its message returns with it. It counts as explaining a
/// block only while it renders, though: on a form with no summary nothing else shows its error, so
/// a blocked submit with every revealed field hidden shows the gate.
/// </summary>
/// <remarks>
/// The clock is a <see cref="FakeTimeProvider"/> that no test advances, so no re-check ever fires:
/// what a test sees after a field-set move is what the move itself did, with no validation run
/// behind it, and the rule counter proves it.
/// </remarks>
public class GateRevealedOffScreenTests : BunitContext
{
    private const string DestinationRequired = "Destination is required";
    private const string TravelerRequired = "Traveler name is required";
    private const string GateText = "information that is not currently displayed is invalid";

    private readonly TripValidator _rules = new();

    public GateRevealedOffScreenTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddFormidableBlazor();
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        Services.AddSingleton<IValidator<Trip>>(_rules);
        Services.AddSingleton<IFormidableFocusService>(new NoFocus());
    }

    // S1. A blocked submit reveals both failing fields, they stop rendering, and a second submit
    // is blocked: on a form with no summary, the model-level list is the only place left that can
    // say why, so it shows the gate. Mutation that must break it: restore the arming to
    // `_gateArmed = disclosed.Count == 0`, and the second submit arms nothing, so the list is
    // empty. Restoring GateActive's ledger-only test breaks it the same way.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_blocked_submit_whose_shown_errors_are_all_off_screen_shows_the_gate(bool attach)
    {
        var trip = new Trip();
        var cut = RenderHost(trip, attach, summary: false, destination: true, traveler: true);

        var first = await SubmitAsync(cut);
        Assert.False(first.CanProceed);
        Assert.Equal([DestinationRequired], FieldMessages(cut, "destination"));
        Assert.Equal([TravelerRequired], FieldMessages(cut, "traveler"));
        Assert.Empty(ModelMessages(cut, trip));

        await ShowAsync(cut, destination: false, traveler: false);
        var second = await SubmitAsync(cut);

        Assert.False(second.CanProceed);
        var gate = Assert.Single(ModelMessages(cut, trip));
        Assert.Contains(GateText, gate);
        AssertGateOnEverySurface(cut, trip);
        Assert.Contains(new FormidableOptions().ModelLevelDisplayName, second.VisibleErrorSummary);

        await Services.DisposeAsync();
    }

    // S4. One field a submit revealed, now hidden, beside a failing field no submit has shown:
    // neither is on screen, so the gate shows. Mutation that must break it: restore the arming
    // to `_gateArmed = disclosed.Count == 0`, and the revealed field alone keeps the gate down.
    [Fact]
    public async Task A_revealed_field_off_screen_beside_one_never_shown_shows_the_gate()
    {
        var trip = new Trip();
        var cut = RenderHost(trip, attach: false, summary: false, destination: true, traveler: false);

        Assert.False((await SubmitAsync(cut)).CanProceed);
        Assert.Equal([DestinationRequired], FieldMessages(cut, "destination"));
        Assert.Empty(ModelMessages(cut, trip));

        await ShowAsync(cut, destination: false);
        Assert.False((await SubmitAsync(cut)).CanProceed);

        var gate = Assert.Single(ModelMessages(cut, trip));
        Assert.Contains(GateText, gate);
        AssertGateOnEverySurface(cut, trip);

        await Services.DisposeAsync();
    }

    // S2. The same route on a form with a summary: the summary keeps listing the hidden fields'
    // errors, since the reveal outlasts the field, and the gate's line shows beside them, since
    // neither field renders. Mutation that must break it: restore GateActive's ledger-only test
    // (`_revealedErrorFields.Overlaps(_submitVerdictErrors.Keys)`), and the summary lists the two
    // errors with no gate line.
    [Fact]
    public async Task A_summary_lists_the_hidden_revealed_errors_and_the_gate_beside_them()
    {
        var trip = new Trip();
        var cut = RenderHost(trip, attach: false, summary: true, destination: true, traveler: true);

        Assert.False((await SubmitAsync(cut)).CanProceed);
        Assert.Equal([DestinationRequired, TravelerRequired], SummaryEntries(cut));

        await ShowAsync(cut, destination: false, traveler: false);
        Assert.False((await SubmitAsync(cut)).CanProceed);

        var entries = SummaryEntries(cut);
        Assert.Equal(3, entries.Count);
        Assert.Equal(DestinationRequired, entries[0]);
        Assert.Equal(TravelerRequired, entries[1]);
        Assert.Contains(GateText, entries[2]);

        await Services.DisposeAsync();
    }

    // Return. After the gate shows for fields that are off screen, the fields render again: their
    // messages are back at once, and the gate leaves the model-level list and the message store
    // in the same move. No rule runs for it, because the move rebuilds what is shown and validates
    // nothing. Mutation that must break it: drop the rebuild OnRenderedFieldsChanged makes while
    // the gate turns on a revealed field's visibility, and the gate stays in the list and the
    // store until a re-check that this clock never fires.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hidden_revealed_fields_that_return_bring_their_messages_back_and_clear_the_gate_without_a_check(bool attach)
    {
        var trip = new Trip();
        var cut = RenderHost(trip, attach, summary: false, destination: true, traveler: true);
        Assert.False((await SubmitAsync(cut)).CanProceed);
        await ShowAsync(cut, destination: false, traveler: false);
        Assert.False((await SubmitAsync(cut)).CanProceed);
        Assert.Single(ModelMessages(cut, trip));
        var runs = _rules.Runs;

        await ShowAsync(cut, destination: true, traveler: true);

        Assert.Equal([DestinationRequired], FieldMessages(cut, "destination"));
        Assert.Equal([TravelerRequired], FieldMessages(cut, "traveler"));
        Assert.Empty(ModelMessages(cut, trip));
        var engine = Engine(cut);
        var modelLevel = new FieldIdentifier(trip, string.Empty);
        Assert.Empty(engine.GetIssues(modelLevel));
        Assert.Empty(engine.EditContext.GetValidationMessages(modelLevel));
        Assert.Equal(runs, _rules.Runs);

        await Services.DisposeAsync();
    }

    // The other direction of the same move. The gate came back down when the fields returned,
    // and the form is still blocked by the very submit that armed it, so hiding the fields again
    // brings the gate back at once, again with no rule run. Mutation that must break it: republish
    // on a move only when it takes the gate down (`GateTurnsOnVisibility && !GateActive`), and the
    // list stays empty after the fields hide. Dropping the rebuild altogether breaks it earlier, at
    // the clearing.
    [Fact]
    public async Task Returned_fields_hidden_again_bring_the_gate_back_without_a_check()
    {
        var trip = new Trip();
        var cut = RenderHost(trip, attach: false, summary: false, destination: true, traveler: true);
        Assert.False((await SubmitAsync(cut)).CanProceed);
        await ShowAsync(cut, destination: false, traveler: false);
        Assert.False((await SubmitAsync(cut)).CanProceed);
        await ShowAsync(cut, destination: true, traveler: true);
        Assert.Empty(ModelMessages(cut, trip));
        var runs = _rules.Runs;

        await ShowAsync(cut, destination: false, traveler: false);

        var gate = Assert.Single(ModelMessages(cut, trip));
        Assert.Contains(GateText, gate);
        AssertGateOnEverySurface(cut, trip);
        Assert.Equal(runs, _rules.Runs);

        await Services.DisposeAsync();
    }

    // A pin: a revealed field that stays on screen keeps the gate down. The
    // second submit blocks with Destination shown and Traveler name hidden, so Destination's own
    // message explains the block. Two guards hold it, so only both mutated together break it:
    // force the arming to `_gateArmed = true` and remove GateActive's revealed-field loop, and
    // the gate shows beside Destination's message. Either mutation alone leaves the other guard
    // holding the gate down.
    [Fact]
    public async Task A_revealed_field_still_on_screen_keeps_the_gate_down()
    {
        var trip = new Trip();
        var cut = RenderHost(trip, attach: false, summary: false, destination: true, traveler: true);
        Assert.False((await SubmitAsync(cut)).CanProceed);

        await ShowAsync(cut, traveler: false);
        Assert.False((await SubmitAsync(cut)).CanProceed);

        Assert.Equal([DestinationRequired], FieldMessages(cut, "destination"));
        Assert.Empty(ModelMessages(cut, trip));
        var modelLevel = new FieldIdentifier(trip, string.Empty);
        Assert.Empty(Engine(cut).EditContext.GetValidationMessages(modelLevel));

        await Services.DisposeAsync();
    }

    private static void AssertGateOnEverySurface(IRenderedComponent<TripHost> cut, Trip trip)
    {
        var engine = Engine(cut);
        var modelLevel = new FieldIdentifier(trip, string.Empty);
        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));
        Assert.Contains(engine.GetVisibleIssues(), v => v.Field.Equals(modelLevel) && v.Issue.Message.Contains(GateText));
        Assert.Contains(engine.EditContext.GetValidationMessages(modelLevel), m => m.Contains(GateText));
    }

    private IRenderedComponent<TripHost> RenderHost(Trip trip, bool attach, bool summary, bool destination, bool traveler) =>
        Render<TripHost>(parameters => parameters
            .Add(p => p.Trip, trip)
            .Add(p => p.Attach, attach)
            .Add(p => p.Summary, summary)
            .Add(p => p.Destination, destination)
            .Add(p => p.Traveler, traveler));

    // The dispatcher runs one piece of work at a time, so the no-op queued after the render
    // completes only once any reconcile that render posted has run.
    private static async Task ShowAsync(IRenderedComponent<TripHost> cut, bool? destination = null, bool? traveler = null)
    {
        await cut.InvokeAsync(() => cut.Render(parameters =>
        {
            if (destination is { } d)
            {
                parameters.Add(p => p.Destination, d);
            }

            if (traveler is { } t)
            {
                parameters.Add(p => p.Traveler, t);
            }
        }));
        await cut.InvokeAsync(() => { });
    }

    private static async Task<SubmitOutcome> SubmitAsync(IRenderedComponent<TripHost> cut)
    {
        var outcome = cut.Instance.Attach
            ? await cut.InvokeAsync(() => cut.FindComponent<FormidableValidator<Trip>>().Instance.ValidateForSubmitAsync())
            : await cut.InvokeAsync(() => cut.FindComponent<FormidableForm<Trip>>().Instance.SubmitAsync());
        await cut.InvokeAsync(() => { });
        return outcome;
    }

    private static IFormidableEngine Engine(IRenderedComponent<TripHost> cut) =>
        cut.Instance.Attach
            ? cut.FindComponent<FormidableValidator<Trip>>().Instance.Engine!
            : cut.FindComponent<FormidableForm<Trip>>().Instance.Engine!;

    private static List<string> ModelMessages(IRenderedComponent<TripHost> cut, Trip trip)
    {
        var id = FormidableFieldId.MessagesFor(new FieldIdentifier(trip, string.Empty));
        var list = Assert.Single(cut.FindAll("ul"), ul => ul.Id == id);
        return list.Children.Select(li => li.TextContent.Trim()).ToList();
    }

    private static List<string> FieldMessages(IRenderedComponent<TripHost> cut, string name) =>
        cut.FindAll($"div[data-field={name}] li").Select(li => li.TextContent.Trim()).ToList();

    private static List<string> SummaryEntries(IRenderedComponent<TripHost> cut) =>
        cut.FindAll("li.formidable-summary__item").Select(li => li.TextContent.Trim()).ToList();

    /// <summary>Two required text fields, each able to stop rendering on its own.</summary>
    public sealed class Trip
    {
        public string Destination { get; set; } = string.Empty;

        public string TravelerName { get; set; } = string.Empty;
    }

    /// <summary>Both fields required, with every rule execution counted.</summary>
    public sealed class TripValidator : DraftSubmitValidator<Trip>
    {
        public int Runs;

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(t => t.Destination).Must(Counted).WithMessage(DestinationRequired);
            RuleFor(t => t.TravelerName).Must(Counted).WithMessage(TravelerRequired);
        }

        private bool Counted(string value)
        {
            Interlocked.Increment(ref Runs);
            return !string.IsNullOrEmpty(value);
        }
    }

    /// <summary>Takes no focus, so a blocked submit's focus move touches no script.</summary>
    private sealed class NoFocus : IFormidableFocusService
    {
        public ValueTask<bool> FocusAsync(FieldIdentifier field) => ValueTask.FromResult(false);
    }

    /// <summary>A form over <see cref="Trip"/>, on either root, with a summary or a model-level list, and each field shown or not.</summary>
    private sealed class TripHost : ComponentBase
    {
        [Parameter]
        public Trip Trip { get; set; } = default!;

        [Parameter]
        public bool Attach { get; set; }

        [Parameter]
        public bool Summary { get; set; }

        [Parameter]
        public bool Destination { get; set; }

        [Parameter]
        public bool Traveler { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Attach)
            {
                builder.OpenComponent<EditForm>(0);
                builder.AddComponentParameter(1, nameof(EditForm.Model), Trip);
                builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableValidator<Trip>>(0);
                    inner.AddComponentParameter(1, nameof(FormidableValidator<Trip>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Content));
                    inner.CloseComponent();
                }));
                builder.CloseComponent();
                return;
            }

            builder.OpenComponent<FormidableForm<Trip>>(10);
            builder.AddComponentParameter(11, nameof(FormidableForm<Trip>.Model), Trip);
            builder.AddComponentParameter(12, nameof(FormidableForm<Trip>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Content));
            builder.CloseComponent();
        }

        private void Content(RenderTreeBuilder builder)
        {
            if (Summary)
            {
                builder.OpenComponent<FormidableSummary>(0);
                builder.CloseComponent();
            }
            else
            {
                builder.OpenComponent<FormidableModelMessage>(1);
                builder.CloseComponent();
            }

            if (Destination)
            {
                AddField(builder, 10, "destination", () => Trip.Destination, v => Trip.Destination = v);
            }

            if (Traveler)
            {
                AddField(builder, 30, "traveler", () => Trip.TravelerName, v => Trip.TravelerName = v);
            }
        }

        private void AddField(RenderTreeBuilder builder, int sequence, string name, Expression<Func<string?>> field, Action<string> set)
        {
            builder.OpenElement(sequence, "div");
            builder.AddAttribute(sequence + 1, "data-field", name);
            builder.OpenComponent<FormidableInputText>(sequence + 2);
            builder.AddComponentParameter(sequence + 3, "For", field);
            builder.AddComponentParameter(sequence + 4, "Value", field.Compile()());
            builder.AddComponentParameter(sequence + 5, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => set(v ?? string.Empty)));
            builder.CloseComponent();
            builder.OpenComponent<FormidableFieldMessage<string?>>(sequence + 6);
            builder.AddComponentParameter(sequence + 7, "For", field);
            builder.CloseComponent();
            builder.CloseElement();
        }
    }
}
