using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Each publish says which fields' views moved (state or messages) and whether something no
/// field owns moved (order, validity, a check running, a submit answered), so a subscriber can
/// skip a change that names none of its fields. The engine computes the set itself; nothing is
/// handed in. An engine with no root raises directly, which is enough to read the arguments.
/// </summary>
public class StateChangedArgsTests
{
    private readonly EngineOrder _order = new() { Customer = new EngineCustomer() };
    private readonly EditContext _editContext;
    private readonly FormidableEngine<EngineOrder> _engine;
    private readonly List<FormidableStateChangedEventArgs> _events = [];

    public StateChangedArgsTests()
    {
        _editContext = new EditContext(_order);
        _engine = Build(_order, _editContext, new FormidableOptions { RefreshDebounce = Timeout.InfiniteTimeSpan });
        _engine.StateChanged += (_, e) => _events.Add(e);
    }

    // The set is computed at every publish, never the shared Empty's null: the touch, the check's
    // start and its verdict each name the edited field, and a registered sibling and the
    // model-level field, whose views do not move, are named by none of them.
    [Fact]
    public async Task A_committed_edit_names_the_edited_field_and_no_other()
    {
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        var customerName = new FieldIdentifier(_order.Customer!, nameof(EngineCustomer.Name));
        _engine.Registry.Register(description);
        _engine.Registry.Register(customerName);

        _editContext.NotifyFieldChanged(description);
        await SettleAsync();

        Assert.True(_events.Count >= 3, $"expected the touch, the begin and the end; saw {_events.Count}");
        Assert.All(_events, e =>
        {
            Assert.NotNull(e.ChangedFields);
            Assert.True(e.Includes(description));
            Assert.False(e.Includes(customerName));
            Assert.DoesNotContain(_engine.ModelLevelField, e.ChangedFields!);
        });
    }

    [Fact]
    public void A_changed_order_is_a_form_level_change_and_names_no_field()
    {
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        _engine.Registry.Register(description);

        _engine.SetFieldOrder(new Dictionary<FieldIdentifier, int> { [description] = 0 });

        var e = Assert.Single(_events);
        Assert.True(e.FormChanged);
        Assert.Empty(e.ChangedFields!);
    }

    [Fact]
    public async Task A_submits_begin_and_end_are_form_level_changes()
    {
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        _engine.Registry.Register(description);

        await _engine.ValidateForSubmitAsync();

        Assert.Contains(_events, e => e.FormChanged);   // IsSubmitting rose, then fell and HasSubmitted rose
        Assert.Contains(_events, e => e.Includes(description));   // the submit's errors landed on it
        Assert.Contains(_events, e => e.Includes(_engine.ModelLevelField));   // "The order is incomplete"
    }

    [Fact]
    public void The_empty_instance_and_a_null_set_mean_every_field()
    {
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));

        Assert.True(FormidableStateChangedEventArgs.Empty.Everything);
        Assert.True(FormidableStateChangedEventArgs.Empty.Includes(description));
        Assert.False(FormidableStateChangedEventArgs.Empty.FormChanged);
        Assert.False(new FormidableStateChangedEventArgs { ChangedFields = new HashSet<FieldIdentifier>() }.Includes(description));
    }

    // A first touch publishes before the handler starts or arms the check that re-answers the
    // submit rules, and the handler counts that check from its first publish on, so a sibling's
    // held vouch is served across the edit at every publish of the sequence: its view never
    // moves, and no publish names it. Before the first submit the check is the live pass the
    // change starts.
    [Fact]
    public async Task A_first_touch_before_a_submit_keeps_a_green_siblings_vouch_and_never_names_it()
    {
        var order = new EngineOrder { Customer = new EngineCustomer { Name = "Bea" } };
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions { TrackFormValidity = true });
        var name = new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        engine.Registry.Register(name);
        engine.Registry.Register(description);
        await WaitUntilAsync(() => !engine.IsValidating && engine.GetFieldState(name).WouldPassSubmit);
        Assert.True(engine.GetFieldState(name).WouldPassSubmit, "setup: the name is not green after the first check lands");

        // Under a root the first render's order resolution publishes once the fields have
        // registered, which is what puts the sibling's green view in the baseline; the test
        // supplies that publish.
        engine.SetFieldOrder(new Dictionary<FieldIdentifier, int> { [name] = 0, [description] = 1 });

        await AssertFirstTouchKeepsSiblingAsync(engine, editContext, sibling: name, edited: description);
    }

    // After a blocked submit the checks on their way are the live pass the change starts and the
    // whole-form re-check it arms; on a mostly clean form every other field is green, and none of
    // them is named by the touch.
    [Fact]
    public async Task A_first_touch_after_a_blocked_submit_keeps_a_green_siblings_vouch_and_never_names_it()
    {
        var order = new EngineOrder { Description = "valid desc", Customer = new EngineCustomer { Name = "Bea" } };
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions());
        var name = new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        engine.Registry.Register(name);
        engine.Registry.Register(description);
        await engine.ValidateForSubmitAsync();   // blocked by the form-level rule; both fields pass their own
        Assert.True(engine.GetFieldState(name).WouldPassSubmit, "setup: the name is not green after the submit lands");

        await AssertFirstTouchKeepsSiblingAsync(engine, editContext, sibling: name, edited: description);
    }

    private static async Task AssertFirstTouchKeepsSiblingAsync(
        FormidableEngine<EngineOrder> engine,
        EditContext editContext,
        FieldIdentifier sibling,
        FieldIdentifier edited)
    {
        var events = new List<FormidableStateChangedEventArgs>();
        var vouches = new List<bool>();
        engine.StateChanged += (_, e) =>
        {
            events.Add(e);
            vouches.Add(engine.GetFieldState(sibling).WouldPassSubmit);
        };

        editContext.NotifyFieldChanged(edited);
        await WaitUntilAsync(() => !engine.IsValidating);
        await Task.Yield();

        Assert.NotEmpty(events);
        for (var i = 0; i < events.Count; i++)
        {
            Assert.False(events[i].Includes(sibling), $"publish {i + 1} of {events.Count} named the sibling");
            Assert.True(vouches[i], $"the sibling read no vouch at publish {i + 1} of {events.Count}");
        }

        Assert.True(engine.GetFieldState(sibling).WouldPassSubmit);
    }

    // Guard for the documented case: under a window that never closes, with tracking off and a
    // RefreshDebounce that never passes after the submit, the edit starts and arms nothing, so
    // nothing is on its way and the vouch is withheld from every other field ("It takes the green
    // off every other field unless another check is already on its way", css-and-accessibility).
    // The sibling's view moves at the touch, so the touch names it.
    [Fact]
    public async Task A_first_touch_with_nothing_on_its_way_takes_a_siblings_green_and_names_it()
    {
        var order = new EngineOrder { Description = "valid desc", Customer = new EngineCustomer { Name = "Bea" } };
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions
        {
            LiveDebounce = Timeout.InfiniteTimeSpan,
            RefreshDebounce = Timeout.InfiniteTimeSpan,
        });
        var name = new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        engine.Registry.Register(name);
        engine.Registry.Register(description);
        await engine.ValidateForSubmitAsync();
        Assert.True(engine.GetFieldState(name).WouldPassSubmit, "setup: the name is not green after the submit lands");
        var events = new List<FormidableStateChangedEventArgs>();
        var vouches = new List<bool>();
        engine.StateChanged += (_, e) =>
        {
            events.Add(e);
            vouches.Add(engine.GetFieldState(name).WouldPassSubmit);
        };

        editContext.NotifyFieldChanged(description);

        Assert.Contains(events, e => e.Includes(name));
        Assert.All(vouches, v => Assert.False(v));
        Assert.False(engine.GetFieldState(name).WouldPassSubmit);
    }

    // The diff reads GetIssues, which under EngagedAndVisible consults DisclosureOverride for an
    // engaged field whether or not anything registers it (IsVisible asks the override before the
    // registry). The override's throw lands where a component's render would have sent it: on the
    // publish's caller, here the test's own call, since there is no root; under a root a live
    // pass's caller is the engine's background observer, which hands it to the boundary
    // (FormidableEngineHandlerThrowTests). The override is consulted only once the field has a
    // live issue (LiveViewOf hands an empty filed list back untouched), so the first check runs
    // with a benign override and the throwing one goes in before a later publish; options are
    // read at each use, which is what lets the test swap it.
    [Fact]
    public async Task A_disclosure_override_that_throws_during_the_diff_reaches_the_publishs_caller()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        var options = new FormidableOptions
        {
            RefreshDebounce = Timeout.InfiniteTimeSpan,
            LiveDisclosure = LiveIssueDisclosure.EngagedAndVisible,
            DisclosureOverride = _ => true,
        };
        using var engine = Build(order, editContext, options);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        editContext.NotifyFieldChanged(description);
        await WaitUntilAsync(() => !engine.IsValidating);

        Assert.NotEmpty(engine.GetIssues(description));

        options.DisclosureOverride = _ => throw new InvalidOperationException("override");
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            engine.SetFieldOrder(new Dictionary<FieldIdentifier, int> { [description] = 0 }));

        Assert.Equal("override", thrown.Message);
    }

    // A field that leaves every live collection (its component gone, never touched, no verdict,
    // no server issue) is compared once more against the view it last published, so the publish
    // that lets it go still names its last move, and then leaves the baseline: a later move of
    // its view while nothing holds it is not named, as a never-held field's is not. Here the
    // arm-less edit withholds the vouch from every field, which drops the departed field's green,
    // and the second submit restores it without a publish naming the field.
    [Fact]
    public async Task A_departed_field_is_named_once_as_its_view_falls_and_not_again()
    {
        var order = new EngineOrder { Description = "valid desc", Customer = new EngineCustomer { Name = "Bea" } };
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions
        {
            LiveDebounce = Timeout.InfiniteTimeSpan,
            RefreshDebounce = Timeout.InfiniteTimeSpan,
        });
        var name = new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var nameRegistration = engine.Registry.Register(name);
        engine.Registry.Register(description);
        await engine.ValidateForSubmitAsync();   // blocked by the form-level rule; the name is green, and the end publish put that view in the baseline
        Assert.True(engine.GetFieldState(name).WouldPassSubmit, "setup: the name is not green after the submit lands");
        nameRegistration.Dispose();
        var events = new List<FormidableStateChangedEventArgs>();
        engine.StateChanged += (_, e) => events.Add(e);

        editContext.NotifyFieldChanged(description);   // nothing is on its way, so every vouch drops at the touch publish

        var touch = Assert.Single(events);
        Assert.True(touch.Includes(name), "the publish that let the departed field go did not name its fall");
        Assert.False(engine.GetFieldState(name).WouldPassSubmit);

        events.Clear();
        await engine.ValidateForSubmitAsync();   // coverage is fresh again, so the departed field reads green once more

        Assert.True(engine.GetFieldState(name).WouldPassSubmit, "setup: the second submit did not restore the vouch");
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.False(e.Includes(name), "a publish named a field nothing holds"));
    }

    // A field that returns to a live collection after the baseline let it go is compared against
    // the default view, as any field new to the baseline is, so one that returns green is named
    // on its return: a render for a component that has just mounted, never a stale view.
    [Fact]
    public async Task A_field_that_returns_green_is_named_on_its_return()
    {
        var order = new EngineOrder { Description = "valid desc", Customer = new EngineCustomer { Name = "Bea" } };
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions { RefreshDebounce = Timeout.InfiniteTimeSpan });
        var name = new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var nameRegistration = engine.Registry.Register(name);
        engine.Registry.Register(description);
        await engine.ValidateForSubmitAsync();   // blocked by the form-level rule; the name is green
        Assert.True(engine.GetFieldState(name).WouldPassSubmit, "setup: the name is not green after the submit lands");
        nameRegistration.Dispose();
        engine.SetFieldOrder(new Dictionary<FieldIdentifier, int> { [description] = 0 });   // the publish that lets the field go
        var events = new List<FormidableStateChangedEventArgs>();
        engine.StateChanged += (_, e) => events.Add(e);

        engine.Registry.Register(name);
        engine.SetFieldOrder(new Dictionary<FieldIdentifier, int> { [description] = 0, [name] = 1 });

        var e = Assert.Single(events);
        Assert.True(e.Includes(name), "the field that returned green was not named");
    }

    private static FormidableEngine<EngineOrder> Build(EngineOrder order, EditContext editContext, FormidableOptions options) =>
        new(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(new DeclarationOrderValidator()),
            new ReflectionModelIntrospector(),
            options,
            new FakeTimeProvider());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    private async Task SettleAsync()
    {
        await WaitUntilAsync(() => !_engine.IsValidating);
        await Task.Yield();
    }
}
