using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the submit hold at the engine: a field a holding registration renders shows no live
/// message on any surface until the form's first answered submit or server apply, while its rules
/// keep running; from then on it answers as any other field.
/// </summary>
public class FormidableEngineSubmitHoldTests
{
    private const string GateText = "not currently displayed";
    private static readonly string TooLong = new('x', 11);

    // Both policies are rows because the hold sits ahead of the default policy's early return, and
    // under EngagedAndVisible a registered field is visible. Mutation: delete the hold condition in
    // LiveViewOf, and both held rows fail.
    [Theory]
    [InlineData(true, LiveIssueDisclosure.Engaged)]
    [InlineData(true, LiveIssueDisclosure.EngagedAndVisible)]
    [InlineData(false, LiveIssueDisclosure.Engaged)]
    [InlineData(false, LiveIssueDisclosure.EngagedAndVisible)]
    public void A_held_field_shows_no_live_message_on_any_surface_before_the_first_submit(
        bool held, LiveIssueDisclosure policy)
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions { LiveDisclosure = policy });
        var description = Field(order, nameof(EngineOrder.Description));
        using var registration = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: held);

        order.Description = TooLong;
        editContext.NotifyFieldChanged(description);

        var state = engine.GetFieldState(description);
        Assert.True(state.IsModified);
        Assert.True(state.IsTouched);
        if (held)
        {
            Assert.Empty(engine.GetIssues(description));
            Assert.False(state.HasErrors);
            Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Field.Equals(description));
            Assert.Empty(editContext.GetValidationMessages(description));
        }
        else
        {
            Assert.Contains(engine.GetIssues(description), i => i.Message.Contains("10"));
            Assert.True(state.HasErrors);
            Assert.Contains(engine.GetVisibleIssues(), v => v.Field.Equals(description));
            Assert.NotEmpty(editContext.GetValidationMessages(description));
        }
    }

    // The rules run while held: the verdict the live pass filed is what the server apply's
    // republish discloses, with no pass in between. Mutations: skip filing a held field's live
    // verdict (rather than holding the view), or make the hold condition ignore HasSubmitted, and
    // the last two asserts fail; delete the hold condition, and the first one fails.
    [Fact]
    public void Rules_run_while_held_and_a_server_apply_lifts_the_hold()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions());
        var description = Field(order, nameof(EngineOrder.Description));
        using var registration = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: true);

        order.Description = TooLong;
        editContext.NotifyFieldChanged(description);
        Assert.Empty(editContext.GetValidationMessages(description));

        engine.ApplyServerIssues([]);

        Assert.True(engine.HasSubmitted);
        Assert.Contains(editContext.GetValidationMessages(description), m => m.Contains("10"));
        Assert.Contains(engine.GetIssues(description), i => i.Message.Contains("10"));
    }

    // The blocked-submit explanation: the first submit reveals the held field (it is registered),
    // names it in the outcome's summary and discloses it everywhere. Mutation: delete the hold
    // condition in LiveViewOf, and the assertion ahead of the submit fails.
    [Fact]
    public async Task A_blocked_first_submit_names_a_held_field_and_discloses_it()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions());
        var description = Field(order, nameof(EngineOrder.Description));
        using var registration = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: true);

        order.Description = TooLong;
        editContext.NotifyFieldChanged(description);
        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Field.Equals(description));

        var outcome = await engine.ValidateForSubmitAsync();

        Assert.False(outcome.CanProceed);
        Assert.Contains("Description", outcome.VisibleErrorSummary);
        Assert.Contains(engine.GetVisibleIssues(), v => v.Field.Equals(description));
        Assert.True(engine.GetFieldState(description).HasErrors);
        Assert.NotEmpty(editContext.GetValidationMessages(description));
    }

    // From the first submit on, the field answers live as any other: after a passing submit (which
    // un-reveals every field, so the submit view is silent), a new failing edit shows at once
    // through the live view alone. Mutation: make the hold condition ignore HasSubmitted, and the
    // assertions after the second edit fail.
    [Fact]
    public async Task After_the_first_submit_a_held_field_answers_live_like_any_other()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions());
        var description = Field(order, nameof(EngineOrder.Description));
        using var registration = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: true);

        order.Description = "ok";
        editContext.NotifyFieldChanged(description);
        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);

        order.Description = TooLong;
        editContext.NotifyFieldChanged(description);

        Assert.Contains(engine.GetIssues(description), i => i.Message.Contains("10"));
        Assert.Contains(engine.GetVisibleIssues(), v => v.Field.Equals(description));
        Assert.NotEmpty(editContext.GetValidationMessages(description));
    }

    // The defensive gate reads the live view through LiveEntries. It can only be armed by an
    // answered submit, which has already lifted the hold, so a held field's live error dissolves
    // the gate exactly as a plain field's does. Mutation: make the hold condition ignore
    // HasSubmitted, and the held row fails.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_held_field_s_live_error_after_the_first_submit_dissolves_the_gate(bool held)
    {
        var order = new EngineOrder { Description = "ok" };
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions());
        var description = Field(order, nameof(EngineOrder.Description));
        var modelLevel = new FieldIdentifier(order, string.Empty);
        using var registration = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: held);

        // The customer is unregistered, so its error is suppressed and the submit discloses
        // nothing: the gate arms.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));

        order.Description = TooLong;
        editContext.NotifyFieldChanged(description);

        Assert.DoesNotContain(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));
        Assert.Contains(engine.GetIssues(description), i => i.Message.Contains("10"));
    }

    // A submit whose validator throws answers nothing, so the hold stands. Mutation: delete the
    // hold condition in LiveViewOf, and the assertions on the field's issues fail.
    [Fact]
    public async Task A_submit_whose_validator_throws_leaves_the_hold_standing()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var validator = new SubmitThrowsValidator();
        using var engine = Build(order, editContext, new FormidableOptions(), validator);
        var description = Field(order, nameof(EngineOrder.Description));
        using var registration = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: true);

        order.Description = TooLong;
        editContext.NotifyFieldChanged(description);

        validator.Throw = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ValidateForSubmitAsync());

        Assert.False(engine.HasSubmitted);
        Assert.Empty(engine.GetIssues(description));
        Assert.Empty(editContext.GetValidationMessages(description));
    }

    // A submit a load displaced answers nothing, so the hold stands; the load engages the held
    // field as it engages any other, and the live pass it runs files a verdict the hold keeps back.
    // The plain row is the control that the staging discloses at all. Mutation: delete the hold
    // condition in LiveViewOf, and the held row fails.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_submit_a_load_displaced_leaves_the_hold_and_the_load_s_messages_are_held(bool held)
    {
        var order = new EngineOrder { Description = "abc" };
        var editContext = new EditContext(order);
        var validator = new GatedValidator();
        using var engine = Build(order, editContext, new FormidableOptions(), validator);
        var description = Field(order, nameof(EngineOrder.Description));
        using var registration = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: held);

        var submit = engine.ValidateForSubmitAsync();
        Assert.Equal(1, validator.Started);

        var load = engine.DiscloseLoadedValuesAsync();
        var displaced = await submit;
        Assert.False(displaced.CanProceed);
        Assert.Empty(displaced.VisibleErrorSummary);

        validator.Gate.SetResult(); // ShouldPass is false: the load's answer fails the description
        await load;

        Assert.False(engine.HasSubmitted);
        Assert.True(engine.GetFieldState(description).IsTouched);
        if (held)
        {
            Assert.Empty(engine.GetIssues(description));
            Assert.Empty(editContext.GetValidationMessages(description));
        }
        else
        {
            Assert.Contains(engine.GetIssues(description), i => i.Message == "async says no");
            Assert.NotEmpty(editContext.GetValidationMessages(description));
        }
    }

    // The pending indicator: IsFieldValidating does not read the live view, so a held field's own
    // check shows pending while it runs; its answer is then held. Mutation: delete the hold
    // condition in LiveViewOf, and the assertions after the check fail.
    [Fact]
    public async Task A_held_field_shows_pending_while_its_check_runs()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var validator = new GatedValidator();
        using var engine = Build(order, editContext, new FormidableOptions(), validator);
        var description = Field(order, nameof(EngineOrder.Description));
        using var registration = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: true);

        order.Description = "abc";
        editContext.NotifyFieldChanged(description);

        Assert.True(engine.GetFieldState(description).IsValidating);

        var quiet = EngineTestSync.Quiescence(engine);
        validator.Gate.SetResult();
        await quiet;

        var state = engine.GetFieldState(description);
        Assert.False(state.IsValidating);
        Assert.False(state.HasErrors);
        Assert.Empty(engine.GetIssues(description));
    }

    // The valid class: WouldPassSubmit does not read the live view, so a held field that passes
    // wears the valid class, and one that fails wears no class at all. Mutation: delete the hold
    // condition in LiveViewOf, and the failing row fails.
    [Theory]
    [InlineData("ok", "formidable-valid")]
    [InlineData("xxxxxxxxxxx", "")]
    public void A_held_field_s_state_class_reads_valid_when_it_passes_and_nothing_when_it_fails(
        string value, string expected)
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions());
        var description = Field(order, nameof(EngineOrder.Description));
        using var registration = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: true);

        order.Description = value;
        editContext.NotifyFieldChanged(description);

        Assert.Equal(expected, FormidableCss.Compute(engine.GetFieldState(description), new FormidableCssClasses()));
    }

    // A field already showing a live error (engaged with no registration of its own, as a native
    // input engages it) gains a holding registration: the engine's reads hide the error at once,
    // and the store, which a native ValidationMessage renders from, follows as the registration
    // arrives. An engine built directly never hears a rendered-field-set change, and the refresh
    // is off, so nothing but the registry's report of the flip can move the store. Mutation: make
    // the engine's held-state handler do nothing, and the store assertion fails.
    [Fact]
    public void A_hold_arriving_on_a_field_with_a_live_error_standing_retracts_it_from_the_store()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(
            order, editContext, new FormidableOptions { RefreshDebounce = Timeout.InfiniteTimeSpan });
        var description = Field(order, nameof(EngineOrder.Description));

        order.Description = TooLong;
        editContext.NotifyFieldChanged(description);
        Assert.NotEmpty(editContext.GetValidationMessages(description));

        using var registration = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: true);

        Assert.Empty(engine.GetIssues(description));
        Assert.Empty(editContext.GetValidationMessages(description));
    }

    // The same republish in the other direction: the holding registration leaves while a plain one
    // keeps the field on the page, so the held error reaches the store as the hold ends. Mutation:
    // make the engine's held-state handler do nothing, and the store assertion fails.
    [Fact]
    public void A_hold_ending_while_the_field_stays_rendered_publishes_its_live_error()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(
            order, editContext, new FormidableOptions { RefreshDebounce = Timeout.InfiniteTimeSpan });
        var description = Field(order, nameof(EngineOrder.Description));
        using var plain = engine.Registry.Register(description);
        var holding = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: true);

        order.Description = TooLong;
        editContext.NotifyFieldChanged(description);
        Assert.Empty(editContext.GetValidationMessages(description));

        holding.Dispose();

        Assert.Contains(engine.GetIssues(description), i => i.Message.Contains("10"));
        Assert.NotEmpty(editContext.GetValidationMessages(description));
    }

    // A held field whose only registration leaves has departed, and a departure is the root's
    // rendered-field-set reconcile to answer: it drops the verdict and republishes then. The
    // flip the registration makes on its way out must publish nothing, or the store would carry
    // the message the field was holding. An engine built directly never gets that reconcile, so
    // the store read right after the dispose is what the flip alone left. Mutation: drop the
    // departure test in the engine's held-state handler, and the store gains the message.
    [Fact]
    public void A_held_field_whose_only_registration_leaves_publishes_nothing()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(
            order, editContext, new FormidableOptions { RefreshDebounce = Timeout.InfiniteTimeSpan });
        var description = Field(order, nameof(EngineOrder.Description));
        var holding = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: true);

        order.Description = TooLong;
        editContext.NotifyFieldChanged(description);
        Assert.Empty(editContext.GetValidationMessages(description));

        var published = 0;
        engine.StateChanged += (_, _) => published++;
        holding.Dispose();

        Assert.False(engine.Registry.IsRegistered(description));
        Assert.Empty(editContext.GetValidationMessages(description));
        Assert.Equal(0, published);
    }

    // A hold starting on a field with nothing filed changes nothing any surface shows, even while
    // another field's live error stands, so it publishes nothing. The registration's own version
    // move raises nothing here either, because an engine built directly never hears the registry's
    // Changed. Mutation: republish on every flip while any live verdict stands, whatever the
    // flipped field filed, and both counts become one.
    [Fact]
    public void A_wait_starting_on_a_field_with_nothing_filed_publishes_nothing()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(
            order, editContext, new FormidableOptions { RefreshDebounce = Timeout.InfiniteTimeSpan });
        var description = Field(order, nameof(EngineOrder.Description));
        var customer = Field(order, nameof(EngineOrder.Customer));

        order.Description = TooLong;
        editContext.NotifyFieldChanged(description);
        Assert.NotEmpty(editContext.GetValidationMessages(description));

        var count = PublishCount.Of(engine, editContext);
        using var registration = engine.Registry.RegisterWithHold(customer, keepRegistered: false, holdsLiveMessages: true);

        Assert.True(engine.Registry.IsHeld(customer));
        Assert.Equal(0, count.StateChanged);
        Assert.Equal(0, count.ValidationStateChanged);
        Assert.NotEmpty(editContext.GetValidationMessages(description));
    }

    // A passing field has nothing to hold back, so a hold starting on it moves no surface.
    // Mutation: drop the issue-count test from the engine's held-state handler, so any engaged
    // field with a filed verdict republishes, and both counts become one.
    [Fact]
    public void A_wait_starting_on_a_passing_field_publishes_nothing()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(
            order, editContext, new FormidableOptions { RefreshDebounce = Timeout.InfiniteTimeSpan });
        var description = Field(order, nameof(EngineOrder.Description));

        order.Description = "ok";
        editContext.NotifyFieldChanged(description);
        Assert.True(engine.GetFieldState(description).IsModified);
        Assert.Empty(engine.GetIssues(description));

        var count = PublishCount.Of(engine, editContext);
        using var registration = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: true);

        Assert.True(engine.Registry.IsHeld(description));
        Assert.Equal(0, count.StateChanged);
        Assert.Equal(0, count.ValidationStateChanged);
    }

    // Any severity counts. A warning never reaches the store, but the kit shows it and re-renders
    // on StateChanged, so a hold starting over a warning owes one publish to take it down. A pin
    // rather than a red-first test: any broader condition publishes here too, so what it guards is
    // a condition too narrow. Mutation: count only error-severity issues in the engine's
    // held-state handler, and both counts fall to zero.
    [Fact]
    public void A_wait_starting_on_a_field_with_only_a_warning_republishes()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(
            order, editContext, new FormidableOptions { RefreshDebounce = Timeout.InfiniteTimeSpan });
        var description = Field(order, nameof(EngineOrder.Description));

        order.Description = "a-b";
        editContext.NotifyFieldChanged(description);
        Assert.All(engine.GetIssues(description), i => Assert.Equal(ValidationSeverity.Warning, i.Severity));
        Assert.Contains(engine.GetIssues(description), i => i.Message == "Avoid hyphens");
        Assert.Empty(editContext.GetValidationMessages(description));

        var count = PublishCount.Of(engine, editContext);
        using var registration = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: true);

        Assert.Empty(engine.GetIssues(description));
        Assert.Equal(1, count.StateChanged);
        Assert.Equal(1, count.ValidationStateChanged);
    }

    // A held field reads as a field with nothing to say: an issue read returns the same shared empty
    // list it returns for the untouched customer, not a list built for the read. Its rules still
    // failed, which the missing valid class shows.
    // Mutation: have LiveViewOf return an empty list in place of null for a held field, and the
    // Assert.Same fails.
    [Fact]
    public void A_held_failing_field_reads_the_shared_empty_issue_list()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions());
        var description = Field(order, nameof(EngineOrder.Description));
        var customer = Field(order, nameof(EngineOrder.Customer));
        using var registration = engine.Registry.RegisterWithHold(description, keepRegistered: false, holdsLiveMessages: true);

        order.Description = TooLong;
        editContext.NotifyFieldChanged(description);

        var state = engine.GetFieldState(description);
        Assert.False(state.WouldPassSubmit);
        Assert.False(state.HasErrors);
        Assert.Same(engine.GetIssues(customer), engine.GetIssues(description));
    }

    private static FieldIdentifier Field(EngineOrder order, string name) => new(order, name);

    /// <summary>Counts the two notifications a store rebuild raises, from the moment it is attached.</summary>
    private sealed class PublishCount
    {
        public int StateChanged;
        public int ValidationStateChanged;

        public static PublishCount Of(FormidableEngine<EngineOrder> engine, EditContext editContext)
        {
            var count = new PublishCount();
            engine.StateChanged += (_, _) => count.StateChanged++;
            editContext.OnValidationStateChanged += (_, _) => count.ValidationStateChanged++;
            return count;
        }
    }

    private static FormidableEngine<EngineOrder> Build(
        EngineOrder order,
        EditContext editContext,
        FormidableOptions options,
        IValidator<EngineOrder>? validator = null) =>
        new(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator ?? new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            options,
            new FakeTimeProvider());

    /// <summary>The description's draft length rule, and a submit rule on the customer that throws once <see cref="Throw"/> is set.</summary>
    private sealed class SubmitThrowsValidator : DraftSubmitValidator<EngineOrder>
    {
        public bool Throw { get; set; }

        protected override void ConfigureDraftRules() =>
            RuleFor(x => x.Description).MaximumLength(10);

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Customer).Must(_ => Throw ? throw new InvalidOperationException("submit blew up") : true);
    }
}
