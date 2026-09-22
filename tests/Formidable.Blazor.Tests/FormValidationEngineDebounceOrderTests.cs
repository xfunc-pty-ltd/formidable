using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;
using static Formidable.Blazor.Tests.Fixtures.EngineTestSync;

namespace Formidable.Blazor.Tests;

/// <summary>
/// One edit after a submit arms two passes at once — a live pass under the live profile and the
/// post-submit refresh under the submit profile. Without <see cref="FormidableOptions.LiveDebounce"/>
/// the live pass runs synchronously, ahead of the refresh's own window, every time. With it set,
/// the two windows close in whatever order their configured widths give them — and the order does
/// not matter, because rule verdicts are keyed by rule and edit stamp: whichever pass fires first
/// executes the stale rules, and the other serves their verdicts from the store. What the engine
/// still enforces is deference to a pass genuinely IN FLIGHT — a window's fire re-arms rather
/// than cancel a running submit or refresh, and a refresh defers to a running live pass — which
/// is supersession hygiene, not execution ordering. Every configuration still has to reach the
/// same verdicts, so each sequence here is run against one assertion set.
/// </summary>
/// <remarks>
/// The assertions name channels rather than list positions: the draft rule's message can only
/// have come from a live pass (a refresh keeps its verdict to the fields the submit made error
/// sites, and the customer's name is never one of them), and the submit rule's message can only
/// have been cleared by a refresh. Where either message sorts among the visible issues is a
/// separate question with its own tests.
/// </remarks>
public class FormValidationEngineDebounceOrderTests
{
    private const string DraftMessage = "Customer name must be four characters or fewer";
    private const string SubmitMessage = "Description needs a named customer";

    [Fact]
    public async Task Live_before_refresh_leaves_both_channels_current()
    {
        // LiveDebounce null (immediate) against the 300 ms RefreshDebounce default: the live pass
        // runs on the edit, the refresh lands after it.
        var customer = new EngineCustomer();
        var order = new EngineOrder { Description = "Quarterly refresh", Customer = customer };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new ChannelSeparatingValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            time);

        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        // The submit blocks on the description, which makes it the one error site a refresh keeps
        // current; the customer's name passes its draft rule at this point.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);

        // One edit breaks the draft rule and clears the submit rule at the same time.
        customer.Name = "far too long";
        editContext.NotifyFieldChanged(customerName);

        // No live debounce, so the live pass has already run: its verdict is on the field while
        // the refresh the same edit armed is still waiting out its window.
        Assert.Contains(engine.GetIssues(customerName), i => i.Message == DraftMessage);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);

        time.Advance(TimeSpan.FromMilliseconds(301)); // the refresh lands second

        AssertBothChannelsCurrent(engine, customerName, description);
    }

    [Fact]
    public async Task A_wide_live_debounce_lets_the_refresh_land_first_and_the_live_pass_reuse_it()
    {
        // LiveDebounce 400 ms against the 300 ms RefreshDebounce default: the refresh window is
        // the narrower one, so the refresh fires first, executes the whole stale selection, and
        // the live window's own pass then has nothing left to run — it publishes the draft
        // rule's verdict from the store. The reversed pass order changes which pass pays for
        // which rule and nothing else.
        var customer = new EngineCustomer();
        var order = new EngineOrder { Description = "Quarterly refresh", Customer = customer };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new ChannelSeparatingValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveDebounce = TimeSpan.FromMilliseconds(400), DisclosureOverride = _ => true },
            time);

        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);

        customer.Name = "far too long";
        editContext.NotifyFieldChanged(customerName);

        // Both windows are open, so neither channel has moved yet.
        Assert.Empty(engine.GetIssues(customerName));
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);

        time.Advance(TimeSpan.FromMilliseconds(300)); // the refresh fires first

        // The submit channel is already current — the refresh executed both rules — while the
        // draft failure it computed waits for the live channel: it is not a submit-time error
        // site, so only a live pass's verdict apply can disclose it.
        Assert.DoesNotContain(engine.GetIssues(description), i => i.Message == SubmitMessage);
        Assert.Empty(engine.GetIssues(customerName));

        time.Advance(TimeSpan.FromMilliseconds(100)); // the live window closes behind it

        AssertBothChannelsCurrent(engine, customerName, description);
    }

    [Fact]
    public async Task Live_before_refresh_settles_an_async_rule_in_both_channels()
    {
        // The same ordering with the shared draft rule asynchronous, and the mirror of the
        // inverted test below: here the LIVE pass is the one in flight when the other's window
        // closes, so the refresh is the one that has to stand down and re-arm rather than cancel
        // it. An async live rule outlasting the refresh window is the whole shape of that branch.
        var customer = new EngineCustomer();
        var order = new EngineOrder { Description = "Quarterly refresh", Customer = customer };
        var validator = new GatedChannelSeparatingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true },
            time);

        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        var submit = engine.ValidateForSubmitAsync();
        validator.Gate.SetResult();
        Assert.False((await submit).CanProceed);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);
        validator.Reset();

        customer.Name = "far too long";
        editContext.NotifyFieldChanged(customerName);

        // The live pass is in flight on the async rule, so the field it covers reads as pending.
        Assert.True(engine.GetFieldState(customerName).IsValidating);

        time.Advance(TimeSpan.FromMilliseconds(301)); // the refresh window closes against the gated live pass

        // Deferred, not started: the live pass is still the one in flight, and the submit channel
        // still carries the verdict only a refresh can clear.
        Assert.True(engine.GetFieldState(customerName).IsValidating);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);

        var liveSettled = Quiescence(engine);
        validator.Gate.SetResult();
        await liveSettled;
        validator.Reset();

        // The live pass landed; the refresh it held up still owes the submit channel its answer.
        Assert.Contains(engine.GetIssues(customerName), i => i.Message == DraftMessage);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);

        var refreshSettled = Quiescence(engine);
        time.Advance(TimeSpan.FromMilliseconds(301)); // the re-armed refresh window closes
        Assert.True(engine.GetFieldState(customerName).IsValidating);
        validator.Gate.SetResult();
        await refreshSettled;

        AssertBothChannelsCurrent(engine, customerName, description);
    }

    [Fact]
    public async Task A_wide_live_debounce_defers_to_the_in_flight_refresh_and_lands_from_the_store()
    {
        // LiveDebounce 400 ms against the 300 ms RefreshDebounce default, with both rules
        // asynchronous: the refresh fires first and is still in flight on its gate when the live
        // window closes. The window must re-arm rather than start a pass that would cancel the
        // refresh (see RefreshInFlight's remarks) — and once the refresh lands, the re-armed
        // window's own pass finds every rule answered at its stamp and publishes the draft
        // verdict without touching the gate at all.
        var customer = new EngineCustomer();
        var order = new EngineOrder { Description = "Quarterly refresh", Customer = customer };
        var validator = new GatedChannelSeparatingValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { LiveDebounce = TimeSpan.FromMilliseconds(400), DisclosureOverride = _ => true },
            time);

        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        var submit = engine.ValidateForSubmitAsync();
        validator.Gate.SetResult();
        Assert.False((await submit).CanProceed);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);
        validator.Reset();

        customer.Name = "far too long";
        editContext.NotifyFieldChanged(customerName);

        time.Advance(TimeSpan.FromMilliseconds(300)); // the refresh fires first and blocks on its rule
        Assert.True(engine.GetFieldState(customerName).IsValidating);

        time.Advance(TimeSpan.FromMilliseconds(100)); // the live window closes against the in-flight refresh

        // Deferred, not started: the refresh is still the one in flight, and the submit channel
        // still carries the verdict only it can clear.
        Assert.True(engine.GetFieldState(customerName).IsValidating);
        Assert.Contains(engine.GetIssues(description), i => i.Message == SubmitMessage);

        var refreshSettled = Quiescence(engine);
        validator.Gate.SetResult();
        await refreshSettled;

        // The refresh landed both rules: the submit channel is current, and the draft failure it
        // computed waits for the live channel to disclose it.
        Assert.DoesNotContain(engine.GetIssues(description), i => i.Message == SubmitMessage);
        Assert.Empty(engine.GetIssues(customerName));

        time.Advance(TimeSpan.FromMilliseconds(400)); // the re-armed window closes; nothing left to execute

        AssertBothChannelsCurrent(engine, customerName, description);
    }

    /// <summary>
    /// The one assertion set every ordering has to reach. Each message names exactly one channel:
    /// only a live pass can put the draft rule's verdict on the customer's name, and only a
    /// refresh can take the submit rule's verdict off the description.
    /// </summary>
    private static void AssertBothChannelsCurrent(
        FormValidationEngine<EngineOrder> engine,
        FieldIdentifier customerName,
        FieldIdentifier description)
    {
        // One read settles the pending indicator for every field: IsFieldValidating is this
        // conjoined with the pass's scope, so a field-level read here could not fail on its own.
        // The reads that CAN fail are the mid-flight ones, asserted where the pass is still open.
        Assert.False(engine.IsValidating);

        // The live channel carries the edit's own verdict.
        Assert.Contains(engine.GetIssues(customerName), i => i.Message == DraftMessage);
        Assert.Contains(engine.EditContext.GetValidationMessages(customerName), m => m == DraftMessage);

        // The submit channel carries the refreshed one: the rule that blocked the submit passes
        // now, so nothing is left holding the form.
        Assert.DoesNotContain(engine.GetIssues(description), i => i.Message == SubmitMessage);
        Assert.Empty(engine.EditContext.GetValidationMessages(description));
    }
}
