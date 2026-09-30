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

    // Mutation: IsHeld returning false always, or RegisterWithHold ignoring the flag, fails this.
    [Fact]
    public void A_holding_registration_holds_its_field_until_disposed()
    {
        var registration = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true);

        Assert.True(_registry.IsHeld(Description));

        registration.Dispose();

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

    // Mutation: Unregister decrementing the held count on a non-holding disposal fails the first
    // assert; never decrementing it fails the Assert.False.
    [Fact]
    public void A_field_is_held_while_any_current_registration_holds_it()
    {
        var holding = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true);
        var plain = _registry.Register(Description);

        plain.Dispose();
        Assert.True(_registry.IsHeld(Description));

        plain = _registry.Register(Description);
        holding.Dispose();
        Assert.False(_registry.IsHeld(Description));
        Assert.True(_registry.IsRegistered(Description));

        plain.Dispose();
    }

    // Mutation: a kept disposal dropping the hold fails the first IsHeld assert; a later non-kept
    // disposal leaving it fails the last.
    [Fact]
    public void A_kept_holding_registration_keeps_holding_until_a_non_kept_disposal()
    {
        _registry.RegisterWithHold(Description, keepRegistered: true, holdsLiveMessages: true).Dispose();

        Assert.True(_registry.IsRegistered(Description));
        Assert.True(_registry.IsHeld(Description));

        _registry.Register(Description).Dispose();

        Assert.False(_registry.IsRegistered(Description));
        Assert.False(_registry.IsHeld(Description));
    }

    // The event behind the republish: raised once per flip, and never for a registration that
    // leaves the field's held state where it was. Mutation: raise it on every holding
    // registration, and the second holding registration adds an entry.
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
        Assert.Equal([Description, Description], raised);
    }

    // Mutation: drop the disposed guard in FieldRegistration.Dispose, and the second dispose ends
    // the other registration's hold.
    [Fact]
    public void A_second_dispose_of_a_holding_registration_changes_nothing()
    {
        var first = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true);
        using var second = _registry.RegisterWithHold(Description, keepRegistered: false, holdsLiveMessages: true);

        first.Dispose();
        first.Dispose();

        Assert.True(_registry.IsHeld(Description));
    }

    // A retention is what a departed keep-registered component leaves; a component registered
    // for the field speaks for it instead. Mutation: have IsHeld count the retention while a
    // registration stands, and the Assert.False after the plain registration fails.
    [Fact]
    public void A_retention_holds_only_while_no_component_is_registered()
    {
        _registry.RegisterWithHold(Description, keepRegistered: true, holdsLiveMessages: true).Dispose();
        Assert.True(_registry.IsHeld(Description));

        var plain = _registry.Register(Description);
        Assert.False(_registry.IsHeld(Description));

        plain.Dispose();
        Assert.False(_registry.IsRegistered(Description));
        Assert.False(_registry.IsHeld(Description));
    }

    // Mutation: raise the event on a registration only when the arriving registration holds,
    // and the plain registration below raises nothing.
    [Fact]
    public void A_plain_registration_over_a_holding_retention_raises_HeldStateChanged()
    {
        _registry.RegisterWithHold(Description, keepRegistered: true, holdsLiveMessages: true).Dispose();
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
