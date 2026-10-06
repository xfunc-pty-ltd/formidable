using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the registry's hold: a field is held while any current registration holds it, a
/// retention left by keep-registered holds only while no registration stands, a registration's
/// hold can change in place, and the registry reports every flip of a field's held state.
/// </summary>
public class FieldRegistryHoldTests
{
    private readonly FieldRegistry _registry = new();
    private readonly EngineOrder _order = new();

    private FieldIdentifier Description => new(_order, nameof(EngineOrder.Description));

    // The hold ends at the settle that follows the dispose, as the root's reconcile runs it: a
    // holding registration that ends leaves its hold standing until then. Mutation: IsHeld
    // returning false always, or RegisterWithHold ignoring the flag, fails this.
    [Fact]
    public void A_holding_registration_holds_its_field_until_disposed()
    {
        var registration = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true);

        Assert.True(_registry.IsHeld(Description));

        registration.Dispose();
        _registry.SettleReleasedHolds();

        Assert.False(_registry.IsHeld(Description));
        Assert.False(_registry.IsRegistered(Description));
    }

    // Mutation: IsHeld returning IsRegistered's answer fails this.
    [Fact]
    public void A_plain_registration_holds_nothing()
    {
        using var registration = _registry.Register(Description);

        Assert.True(_registry.IsRegistered(Description));
        Assert.False(_registry.IsHeld(Description));
    }

    // A holding registration's hold stands until the settle the root's reconcile runs, so a
    // settle follows each dispose here, and what holds after it is the registrations standing.
    // Mutation: Unregister decrementing the held count on a non-holding disposal fails the first
    // assert; never decrementing it fails the Assert.False.
    [Fact]
    public void A_field_is_held_while_any_current_registration_holds_it()
    {
        var holding = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true);
        var plain = _registry.Register(Description);

        plain.Dispose();
        _registry.SettleReleasedHolds();
        Assert.True(_registry.IsHeld(Description));

        plain = _registry.Register(Description);
        holding.Dispose();
        _registry.SettleReleasedHolds();
        Assert.False(_registry.IsHeld(Description));
        Assert.True(_registry.IsRegistered(Description));

        plain.Dispose();
    }

    // The settle after the kept disposal ends the hold the disposal left standing, so what holds
    // afterwards is the retention alone. Mutation: a kept disposal dropping the hold fails the
    // first IsHeld assert; a later non-kept disposal leaving it fails the last.
    [Fact]
    public void A_kept_holding_registration_keeps_holding_until_a_non_kept_disposal()
    {
        _registry.RegisterWithHold(Description, keepRegistered: true, holdsLiveMessages: true).Dispose();
        _registry.SettleReleasedHolds();

        Assert.True(_registry.IsRegistered(Description));
        Assert.True(_registry.IsHeld(Description));

        _registry.Register(Description).Dispose();

        Assert.False(_registry.IsRegistered(Description));
        Assert.False(_registry.IsHeld(Description));
    }

    // The event behind the republish: raised once per flip, and never for a registration that
    // leaves the field's held state where it was. The last holding registration's hold ends at
    // the settle that follows its dispose, as the root's reconcile runs it. Mutation: raise it on
    // every holding registration, and the second holding registration adds an entry.
    [Fact]
    public void HeldStateChanged_fires_only_when_a_field_starts_or_stops_being_held()
    {
        var raised = new List<FieldIdentifier>();
        _registry.HeldStateChanged += raised.Add;

        using var plain = _registry.Register(Description);
        Assert.Empty(raised);

        var holding = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true);
        Assert.Equal([Description], raised);

        var second = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true);
        holding.Dispose();
        Assert.Equal([Description], raised); // still held by the second

        second.Dispose();
        _registry.SettleReleasedHolds();
        Assert.Equal([Description, Description], raised);
    }

    // The settle after the disposals ends the hold the first one left standing, so what holds
    // afterwards is the second registration alone. Mutation: drop the disposed guard in
    // FieldRegistration.Dispose, and the second dispose ends the other registration's hold.
    [Fact]
    public void A_second_dispose_of_a_holding_registration_changes_nothing()
    {
        var first = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true);
        using var second = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true);

        first.Dispose();
        first.Dispose();
        _registry.SettleReleasedHolds();

        Assert.True(_registry.IsHeld(Description));
    }

    // A retention is what a departed keep-registered component leaves; a component registered
    // for the field speaks for it instead, and starts a new visit. The settle after the kept
    // disposal ends the hold the disposal left standing, so what holds afterwards is the
    // retention alone. Mutation: have IsHeld count the retention while a registration stands and
    // never forget the kept-hold count when a visit starts, together, and the Assert.False after
    // the plain registration fails.
    [Fact]
    public void A_retention_holds_only_while_no_component_is_registered()
    {
        _registry.RegisterWithHold(Description, keepRegistered: true, holdsLiveMessages: true).Dispose();
        _registry.SettleReleasedHolds();
        Assert.True(_registry.IsHeld(Description));

        var plain = _registry.Register(Description);
        Assert.False(_registry.IsHeld(Description));

        plain.Dispose();
        Assert.False(_registry.IsRegistered(Description));
        Assert.False(_registry.IsHeld(Description));
    }

    // The settle after the kept disposal ends the hold the disposal left standing, so the plain
    // registration arrives over the retention alone. Mutation: raise the event on a registration
    // only when the arriving registration holds, and the plain registration below raises nothing.
    [Fact]
    public void A_plain_registration_over_a_holding_retention_raises_HeldStateChanged()
    {
        _registry.RegisterWithHold(Description, keepRegistered: true, holdsLiveMessages: true).Dispose();
        _registry.SettleReleasedHolds();
        var raised = new List<FieldIdentifier>();
        _registry.HeldStateChanged += raised.Add;

        using var plain = _registry.Register(Description);

        Assert.Equal([Description], raised);
    }

    // Mutation: make FieldRegistry.ChangeHold do nothing, and the field is never held.
    [Fact]
    public void ChangeHold_moves_a_field_in_and_out_of_the_held_set()
    {
        using var registration = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: false);
        var raised = new List<FieldIdentifier>();
        _registry.HeldStateChanged += raised.Add;

        registration.ChangeHold(true);
        Assert.True(_registry.IsHeld(Description));
        Assert.Equal([Description], raised);

        registration.ChangeHold(false);
        Assert.False(_registry.IsHeld(Description));
        Assert.Equal([Description, Description], raised);
    }

    // Mutation: raise the event on every ChangeHold, and the list gains an entry.
    [Fact]
    public void ChangeHold_on_one_of_two_holding_registrations_raises_nothing()
    {
        using var first = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true);
        using var second = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true);
        var raised = new List<FieldIdentifier>();
        _registry.HeldStateChanged += raised.Add;

        first.ChangeHold(false);

        Assert.True(_registry.IsHeld(Description));
        Assert.Empty(raised);
    }

    // A change of hold is not a change of which fields are on the page, so nothing a root keys on
    // the registration set moves for it. Mutation: increment the version and raise Changed in
    // FieldRegistry.ChangeHold, and both asserts fail.
    [Fact]
    public void ChangeHold_moves_neither_the_version_nor_Changed()
    {
        using var registration = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: false);
        var version = _registry.Version;
        var changed = 0;
        _registry.Changed += () => changed++;

        registration.ChangeHold(true);
        registration.ChangeHold(false);

        Assert.Equal(version, _registry.Version);
        Assert.Equal(0, changed);
    }

    // The retention holds only if the registration held when it ended. Mutation: have
    // FieldRegistration.ChangeHold leave its own flag, so Dispose passes the hold it registered
    // with, and the retention holds.
    [Fact]
    public void A_retention_left_after_the_hold_was_switched_off_does_not_hold()
    {
        var registration = _registry.RegisterWithHold(Description, keepRegistered: true, holdsLiveMessages: true);

        registration.ChangeHold(false);
        registration.Dispose();

        Assert.True(_registry.IsRegistered(Description));
        Assert.False(_registry.IsHeld(Description));
    }

    // Another registration stands, so a stray change of hold would reach the field's count.
    // Mutation: drop the disposed test in FieldRegistration.ChangeHold, and the field is held.
    [Fact]
    public void ChangeHold_after_dispose_changes_nothing()
    {
        using var standing = _registry.Register(Description);
        var ended = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: false);
        ended.Dispose();
        var raised = new List<FieldIdentifier>();
        _registry.HeldStateChanged += raised.Add;

        ended.ChangeHold(true);

        Assert.False(_registry.IsHeld(Description));
        Assert.Empty(raised);
    }

    // A holding registration that ends leaves its hold standing until the root's reconcile
    // settles the field, so the field cannot show what it held while the reconcile decides
    // whether it left. A plain registration stands throughout, so the field is still on the page
    // and the settle ends the hold. Mutation: end the hold inside Unregister, and the field is no
    // longer held straight after the dispose.
    [Fact]
    public void An_ended_holding_registration_holds_until_settled()
    {
        using var plain = _registry.Register(Description);
        var holding = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true);
        var raised = new List<FieldIdentifier>();
        _registry.HeldStateChanged += raised.Add;

        holding.Dispose();

        Assert.True(_registry.IsHeld(Description));
        Assert.Empty(raised);

        _registry.SettleReleasedHolds();

        Assert.False(_registry.IsHeld(Description));
        Assert.Equal([Description], raised);
    }

    // A kept waiting wrapper around a kept plain input: the wrapper is disposed first, so the
    // plain input is the last to end. Every kept holding registration of the visit counts toward
    // the retention, not only the last to end. Mutation: take KeptHolds from the last registration
    // alone, and the retention does not hold.
    [Fact]
    public void A_kept_waiting_wrapper_keeps_the_retention_held_when_a_plain_kept_input_leaves_last()
    {
        var holding = _registry.RegisterWithHold(Description, keepRegistered: true, holdsLiveMessages: true);
        var plain = _registry.Register(Description, keepRegistered: true);

        holding.Dispose();
        plain.Dispose();
        _registry.SettleReleasedHolds();

        Assert.True(_registry.IsHeld(Description));
        Assert.True(_registry.IsRegistered(Description));
    }

    // A pin: a visit runs from a registration arriving on a field with none standing to the last
    // one ending. A kept plain registration arriving over a holding retention starts a new visit,
    // and its own kept ending leaves a retention that does not hold. Mutation: never forget the
    // count when a visit starts, and the field stays held.
    [Fact]
    public void A_retention_counts_only_the_holds_its_own_visit_ended()
    {
        _registry.RegisterWithHold(Description, keepRegistered: true, holdsLiveMessages: true).Dispose();
        _registry.SettleReleasedHolds();
        Assert.True(_registry.IsHeld(Description));

        _registry.Register(Description, keepRegistered: true).Dispose();
        _registry.SettleReleasedHolds();

        Assert.True(_registry.IsRegistered(Description));
        Assert.False(_registry.IsHeld(Description));
    }

    // A component registered for the field speaks for it, even when a kept waiting component left
    // during the same visit and an earlier visit ended kept. A kept plain registration ends (the
    // field is kept); later a kept waiting wrapper and a plain input register, and the wrapper
    // alone is removed. Once settled, the input standing on the page answers live. Mutation: have
    // IsHeld count the retention while a registration stands, and the field stays held.
    [Fact]
    public void A_standing_registration_speaks_for_a_field_whose_kept_waiting_component_left_mid_visit()
    {
        _registry.Register(Description, keepRegistered: true).Dispose();
        var wrapper = _registry.RegisterWithHold(Description, keepRegistered: true, holdsLiveMessages: true);
        using var input = _registry.Register(Description);

        wrapper.Dispose();
        _registry.SettleReleasedHolds();

        Assert.Equal([Description], _registry.RegisteredFields);
        Assert.False(_registry.IsHeld(Description));
    }

    // A handler of HeldStateChanged that throws while one field settles does not cost another
    // field its settle: every released hold ends, every flip is raised, and the throw reaches the
    // caller afterwards. Mutation: let the first throw leave the settle loop, and the second
    // field's flip is never raised.
    [Fact]
    public void A_throwing_held_state_handler_still_settles_every_other_field()
    {
        var customer = new FieldIdentifier(_order, nameof(EngineOrder.Customer));
        using var descriptionInput = _registry.Register(Description);
        using var customerInput = _registry.Register(customer);
        _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true).Dispose();
        _registry.RegisterWithHold(customer, keepRegistered: false, holdsLiveMessages: true).Dispose();
        var raised = new List<FieldIdentifier>();
        _registry.HeldStateChanged += field =>
        {
            raised.Add(field);
            if (raised.Count == 1)
            {
                throw new InvalidOperationException("a handler throws");
            }
        };

        Assert.Throws<InvalidOperationException>(_registry.SettleReleasedHolds);

        Assert.Equal(2, raised.Count);
        Assert.Contains(Description, raised);
        Assert.Contains(customer, raised);
        Assert.False(_registry.IsHeld(Description));
        Assert.False(_registry.IsHeld(customer));
    }

    // When the handler throws for every field it settles, every throw reaches the caller, together.
    // Mutation: rethrow only the first throw, and the AggregateException never comes.
    [Fact]
    public void Throws_from_several_settling_fields_reach_the_caller_together()
    {
        var customer = new FieldIdentifier(_order, nameof(EngineOrder.Customer));
        using var descriptionInput = _registry.Register(Description);
        using var customerInput = _registry.Register(customer);
        _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true).Dispose();
        _registry.RegisterWithHold(customer, keepRegistered: false, holdsLiveMessages: true).Dispose();
        _registry.HeldStateChanged += field => throw new InvalidOperationException(field.FieldName);

        var thrown = Assert.Throws<AggregateException>(_registry.SettleReleasedHolds);

        Assert.Equal(2, thrown.InnerExceptions.Count);
        Assert.False(_registry.IsHeld(Description));
        Assert.False(_registry.IsHeld(customer));
    }

    // RegisteredFields lists what is on the page; a retention keeps the field registered without
    // an element of its own. Mutation: have RegisteredFields list every entry, and the kept
    // field's Assert.Empty fails.
    [Fact]
    public void A_field_gone_from_every_registration_leaves_no_entry()
    {
        _registry.Register(Description).Dispose();

        Assert.False(_registry.IsRegistered(Description));
        Assert.Empty(_registry.RegisteredFields);

        _registry.Register(Description, keepRegistered: true).Dispose();

        Assert.True(_registry.IsRegistered(Description));
        Assert.Empty(_registry.RegisteredFields);
    }
}
