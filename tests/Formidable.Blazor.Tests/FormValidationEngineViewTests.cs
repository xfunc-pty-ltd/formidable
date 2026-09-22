using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the disclosure channels as views over source state: the defensive gate is a predicate no
/// refresh can delete and no error on screen leaves standing, the reveal ledgers merge by union
/// so a once-revealed field stays watched until reset or a successful submit, the server verdict
/// is its own source replaced wholesale per apply and cleared by every submit, refresh and load,
/// and the live channel discloses an engaged field's verdict on every surface with no
/// registration filtering — the bridge default, stated as contract.
/// </summary>
public class FormValidationEngineViewTests
{
    private const string GateText = "not currently displayed";

    [Fact]
    public async Task The_gate_survives_a_post_submit_refresh_while_the_form_stays_blocked()
    {
        // Nothing registered and no override: every failing field is hidden, so the submit blocks
        // behind the defensive gate's form-level explanation alone.
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(order, editContext, new FormidableOptions(), time);
        var modelLevel = new FieldIdentifier(order, string.Empty);

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));

        // A post-submit edit that leaves the form exactly as blocked as it was: the description
        // takes a value its rules accept, so the customer nothing rendered is still the only
        // failure and still the only thing no surface can account for. The edit engages the
        // description, and the live channel answers for it with silence, which is what leaves
        // the gate the sole explanation of the block. The refresh then replaces the submit
        // channel's source wholesale — and the gate is derived from that source rather than
        // filed beside it, so there is no entry for the replacement to drop. The assertions
        // below read the model-level field alone, which only the gate ever speaks for.
        order.Description = "ok";
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));
        Assert.Contains(
            engine.GetVisibleIssues(),
            v => v.Field.Equals(modelLevel) && v.Issue.Message.Contains(GateText));
        Assert.Contains(editContext.GetValidationMessages(modelLevel), m => m.Contains(GateText));
    }

    [Fact]
    public async Task A_field_revealed_at_an_earlier_submit_rediscloses_when_it_breaks_again()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        // The live channel is narrowed to the draft bucket so the submit channel is the only one
        // that can speak for the description below: engagement alone would otherwise disclose the
        // required-field error, and the reveal ledger this test exists for would stop being what
        // the returning message proves.
        using var engine = Build(
            order, editContext, new FormidableOptions { LiveProfile = ValidationProfile.Draft }, time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var descReg = engine.Registry.Register(description);

        // Submit #1 blocks with the description's error disclosed: the field is revealed.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.NotEmpty(editContext.GetValidationMessages(description));

        // Fix it, and register the customer so submit #2 blocks on a DIFFERENT disclosed field
        // while the description passes.
        order.Description = "ok";
        using var custReg = engine.Registry.Register(new FieldIdentifier(order, nameof(EngineOrder.Customer)));
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Empty(editContext.GetValidationMessages(description));

        // Break the description again and let the refresh land. Reveal state merges by union: a
        // field revealed at ANY blocked submit stays watched until a successful submit resets the
        // ledger, so the refreshed error returns without a third submit — the live channel says
        // nothing here (the description's draft rule passes on an empty value), which is what
        // makes the returning message the submit channel's own.
        order.Description = string.Empty;
        editContext.NotifyFieldChanged(description);
        time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        Assert.NotEmpty(editContext.GetValidationMessages(description));
        Assert.Contains(engine.GetIssues(description), i => i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public async Task A_successful_submit_resets_the_reveal_ledger()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        // Narrowed for the same reason its sibling above is: the ledger is a submit-channel fact,
        // and only a live channel that says nothing about the description can leave the silence
        // below attributable to it.
        using var engine = Build(
            order, editContext, new FormidableOptions { LiveProfile = ValidationProfile.Draft }, time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var descReg = engine.Registry.Register(description);

        // Reveal the description at a blocked submit, then fix everything and submit clean.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        order.Description = "ok";
        order.Customer = new EngineCustomer();
        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);

        // Break the description again and let the refresh land: a successful submit un-reveals
        // wholesale, so the union that keeps a field watched across BLOCKED submits does not keep
        // it watched across a passing one — the error waits for the next submit.
        order.Description = string.Empty;
        editContext.NotifyFieldChanged(description);
        time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        Assert.Empty(editContext.GetValidationMessages(description));
        Assert.Empty(engine.GetIssues(description));
    }

    [Fact]
    public async Task An_advisory_site_revealed_at_an_earlier_submit_keeps_its_advisory_through_later_submits()
    {
        // The advisory ledger's half of the union contract. The description's hyphen warning is
        // disclosed at submit #1; at submit #2 the description is warning-free, so a ledger that
        // merely re-froze to submit #2's advisory sites would forget the field.
        var order = new EngineOrder { Description = "a-b" };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(
            order, editContext, new FormidableOptions { DisclosureOverride = _ => true }, time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        // Submit #1: blocked on the missing customer, with the hyphen warning revealed.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains(engine.GetIssues(description), i => i.Message == "Avoid hyphens");

        // Submit #2: still blocked on the customer, but the description now carries no advisory.
        order.Description = "ok";
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.DoesNotContain(engine.GetIssues(description), i => i.Message == "Avoid hyphens");

        // The warning comes back and the refresh lands: the field was an advisory site at an
        // earlier submit, and reveal state merges by union, so the refreshed warning shows again.
        order.Description = "a-b";
        editContext.NotifyFieldChanged(description);
        time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        Assert.Contains(engine.GetIssues(description), i => i.Message == "Avoid hyphens");
    }

    [Fact]
    public async Task The_gate_dissolves_when_a_server_error_shows()
    {
        // The gate exists to explain a block that would otherwise show nothing. The moment a
        // server-declared error is on screen, something IS displayed, so the explanation must go.
        // Location is the field to aim this at: no client rule speaks for it, so the apply cannot
        // also reveal a client error the gate would have had to dissolve for anyway. The server
        // source alone is what the assertions below are left reading — the mutation being the
        // loss of the server-source conjunct from the gate predicate, which a field the client
        // itself fails would go on hiding.
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(order, editContext, new FormidableOptions(), time);
        var modelLevel = new FieldIdentifier(order, string.Empty);
        var location = new FieldIdentifier(order, nameof(EngineOrder.Location));

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));
        Assert.Empty(engine.GetIssues(location)); // the client says nothing about this field at all

        engine.ApplyServerIssues([new ValidationIssue("Location", "Server rejected this location")]);

        Assert.Contains("Server rejected this location", editContext.GetValidationMessages(location));
        Assert.DoesNotContain(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));
        Assert.DoesNotContain(editContext.GetValidationMessages(modelLevel), m => m.Contains(GateText));
    }

    [Fact]
    public async Task The_gate_dissolves_when_a_live_error_shows_on_a_rendered_field()
    {
        // The live channel explains a block on its own: a field the visitor has committed a
        // change to discloses whatever would fail a submit, and the gate's sentence — that the
        // invalid information is not currently displayed — is false while one of those errors is
        // on screen. The description is rendered and passes, so the submit below blocks on the
        // unregistered customer alone and arms the gate; breaking the description afterwards is
        // the collision, and the gate is what gives way.
        var order = new EngineOrder { Description = "ok" };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(order, editContext, new FormidableOptions(), time);
        var modelLevel = new FieldIdentifier(order, string.Empty);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var descReg = engine.Registry.Register(description);

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));

        order.Description = string.Empty;
        editContext.NotifyFieldChanged(description);

        // The field's own error is on all three surfaces, which is what leaves the gate nothing
        // to stand in for.
        Assert.Contains(engine.GetIssues(description), i => i.Severity == ValidationSeverity.Error);
        Assert.Contains(
            engine.GetVisibleIssues(),
            v => v.Field.Equals(description) && v.Issue.Severity == ValidationSeverity.Error);
        Assert.NotEmpty(editContext.GetValidationMessages(description));

        Assert.DoesNotContain(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));
        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Issue.Message.Contains(GateText));
        Assert.DoesNotContain(editContext.GetValidationMessages(modelLevel), m => m.Contains(GateText));
    }

    [Fact]
    public async Task A_visible_warning_leaves_the_gate_standing()
    {
        // Severity decides, not the mere presence of a message: a warning says nothing about why
        // a submit was refused, so an advisory on screen leaves the gate with exactly as much to
        // explain as it had without one. Same staging as the live-error pin above — a rendered
        // description that passes at submit, an unregistered customer that arms the gate — with
        // the description then engaged into its hyphen warning instead of into an error.
        var order = new EngineOrder { Description = "ok" };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(order, editContext, new FormidableOptions(), time);
        var modelLevel = new FieldIdentifier(order, string.Empty);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var descReg = engine.Registry.Register(description);

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));

        order.Description = "a-b";
        editContext.NotifyFieldChanged(description);

        Assert.Contains(engine.GetIssues(description), i => i.Message == "Avoid hyphens");
        Assert.DoesNotContain(engine.GetIssues(description), i => i.Severity == ValidationSeverity.Error);

        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));
        Assert.Contains(editContext.GetValidationMessages(modelLevel), m => m.Contains(GateText));
    }

    [Fact]
    public async Task The_opt_in_keeps_the_gate_standing_for_a_live_error_it_hides()
    {
        // The gate reads the live channel through the same LiveDisclosure policy every other
        // live surface reads it through, which is the whole of what keeps them agreeing. Under
        // EngagedAndVisible an engaged field nothing renders discloses nowhere, so its error
        // accounts for no more of the block than an unrevealed submit error does — and a gate
        // that gave way to it would leave the submit refusing with not one message anywhere,
        // the silent no-op the gate exists to prevent. The mutation: reading the filed verdicts
        // directly rather than through the policy.
        var order = new EngineOrder { Description = "ok" };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(
            order, editContext,
            new FormidableOptions { LiveDisclosure = LiveIssueDisclosure.EngagedAndVisible },
            time);
        var modelLevel = new FieldIdentifier(order, string.Empty);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));

        order.Description = string.Empty;
        editContext.NotifyFieldChanged(description);

        // The engaged field's error is filed and hidden: nothing renders it, so no surface
        // carries it and the gate is still the only account of the block there is.
        Assert.Empty(engine.GetIssues(description));
        Assert.Empty(editContext.GetValidationMessages(description));

        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));
        Assert.Contains(editContext.GetValidationMessages(modelLevel), m => m.Contains(GateText));
    }

    [Fact]
    public async Task The_gate_dissolves_when_the_form_passes()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(order, editContext, new FormidableOptions(), time);
        var modelLevel = new FieldIdentifier(order, string.Empty);

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));

        // Fix everything the hidden rules were failing on, and let the refresh land: the form no
        // longer blocks, so there is nothing left for the gate to explain.
        order.Description = "ok";
        order.Customer = new EngineCustomer();
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        Assert.Empty(engine.GetIssues(modelLevel));
        Assert.Empty(engine.GetVisibleIssues());
        Assert.Empty(editContext.GetValidationMessages());
    }

    [Fact]
    public async Task A_hidden_error_appearing_after_a_clean_submit_raises_no_gate()
    {
        // The gate speaks only for a submit that was blocked with nothing disclosed. A submit
        // that PASSED disclosed everything there was; a rule that starts failing afterwards on a
        // never-revealed field stays quiet until the next submit — submit is the disclosure
        // event — and no form-level explanation appears in its place.
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(order, editContext, new FormidableOptions(), time);

        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);

        order.Customer = null;
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        Assert.Empty(engine.GetVisibleIssues());
        Assert.Empty(editContext.GetValidationMessages());
    }

    [Fact]
    public async Task The_gate_stands_through_a_live_pass_that_fixes_nothing_and_dissolves_once_the_live_pass_fixes_everything()
    {
        // All-suppressed route: nothing registered, so the description's and the customer's
        // failures are both hidden behind the gate's single explanation.
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions(), new FakeTimeProvider());
        var modelLevel = new FieldIdentifier(order, string.Empty);
        var location = new FieldIdentifier(order, nameof(EngineOrder.Location));

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));

        // Location carries no rule at all, so committing a change to it engages a field and
        // starts a live pass that answers for the whole model — a rebuild that touches nothing
        // the two hidden failures depend on, leaving the gate exactly as armed as it was.
        editContext.NotifyFieldChanged(location);

        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message.Contains(GateText));

        // Fix both hidden failures and notify only one of the two fields that changed: the live
        // pass this starts answers for the whole model regardless of which field triggered it, so
        // the gate dissolves at THIS pass rather than waiting on a second submit.
        order.Description = "ok";
        order.Customer = new EngineCustomer();
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));

        Assert.Empty(engine.GetIssues(modelLevel));
        Assert.Empty(engine.GetVisibleIssues());
        Assert.Empty(editContext.GetValidationMessages());
    }

    [Fact]
    public void A_live_pass_never_arms_the_gate()
    {
        // No submit has ever run here, so _gateArmed defaults false. The live channel runs the
        // submit profile by default, so this edit's own pass answers with both hidden failures —
        // description and customer — in its report; that report's content must not be able to
        // stand a gate up with no submit behind it. Location carries no rule, so engaging it
        // discloses nothing of its own and leaves the gate the only thing left to check for.
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions(), new FakeTimeProvider());
        var modelLevel = new FieldIdentifier(order, string.Empty);

        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Location)));

        Assert.Empty(engine.GetIssues(modelLevel));
        Assert.Empty(engine.GetVisibleIssues());
        Assert.Empty(editContext.GetValidationMessages());
    }

    [Fact]
    public void An_engaged_unrendered_fields_live_error_reaches_every_surface()
    {
        // The bridge default, stated as contract: the live channel discloses an engaged field's
        // verdict on every surface — the engine's own reads AND the EditContext store a native
        // ValidationMessage renders from — and registration filters it nowhere, beyond ending the
        // engagement of a field that leaves the page. Nothing here ever registers a field, so
        // nothing here can leave one either, which is the whole subject below.
        var order = new EngineOrder { Description = new string('x', 11) };
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions(), new FakeTimeProvider());
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        Assert.Equal(LiveIssueDisclosure.Engaged, engine.Options.LiveDisclosure); // the default IS this contract

        editContext.NotifyFieldChanged(description);

        // A rendered-field-set change somewhere else on the form — a conditional section opening,
        // a collection row arriving, a virtualized panel scrolling — reaches the engine on any
        // mixed page, and the anchor-free native input this contract exists for lives on exactly
        // such pages. The engagement prune has to tell a field that LEFT the page from one that
        // was never on it: dropping the second retracts its live verdict from every surface, so
        // the bridge default would hold only until the first registration moved.
        engine.OnRenderedFieldsChanged();

        Assert.Contains(engine.GetIssues(description), i => i.Severity == ValidationSeverity.Error);
        Assert.Contains(engine.GetVisibleIssues(), v => v.Field.Equals(description));
        Assert.NotEmpty(editContext.GetValidationMessages(description));
    }

    [Fact]
    public void A_departed_fields_live_error_leaves_every_surface()
    {
        // The other side of the departure test, so keeping a never-registered field's verdict
        // cannot be read as keeping every unregistered field's. This field DID render, so its
        // unregistering is a departure: the engagement goes, the filed verdict goes with it, and
        // the store the same view projects loses the message. The mutation: a prune that kept
        // every unregistered field — the over-broad reading of the never-registered carve-out
        // beside this — would leave all three standing.
        var order = new EngineOrder { Description = new string('x', 11) };
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions(), new FakeTimeProvider());
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        var registration = engine.Registry.Register(description);
        editContext.NotifyFieldChanged(description);
        Assert.NotEmpty(editContext.GetValidationMessages(description));

        registration.Dispose();
        engine.OnRenderedFieldsChanged();

        Assert.Empty(engine.GetIssues(description));
        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Field.Equals(description));
        Assert.Empty(editContext.GetValidationMessages(description));
    }

    [Fact]
    public void The_opt_in_hides_an_engaged_unrendered_fields_live_error_on_every_surface()
    {
        // LiveIssueDisclosure.EngagedAndVisible gates each live issue on override-aware
        // visibility, uniformly: the issue reads, the severity scan behind the state classes,
        // the visible-issue collection, and the message-store projection must all hide an
        // engaged field nothing renders — a single surface still showing it is the mutation
        // this pins against.
        var order = new EngineOrder { Description = new string('x', 11) };
        var editContext = new EditContext(order);
        using var engine = Build(
            order, editContext,
            new FormidableOptions { LiveDisclosure = LiveIssueDisclosure.EngagedAndVisible },
            new FakeTimeProvider());
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        editContext.NotifyFieldChanged(description);

        Assert.Empty(engine.GetIssues(description));
        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Field.Equals(description));
        Assert.False(engine.GetFieldState(description).HasErrors);
        Assert.Empty(editContext.GetValidationMessages(description));
    }

    [Fact]
    public void The_opt_in_discloses_the_live_error_once_the_field_renders()
    {
        // The other half of what opting in asks for: the moment the field registers, every
        // surface answers again — the store included, which owes its republish to the field-set
        // change, since under this policy registration is one of the live view's own inputs.
        var order = new EngineOrder { Description = new string('x', 11) };
        var editContext = new EditContext(order);
        using var engine = Build(
            order, editContext,
            new FormidableOptions { LiveDisclosure = LiveIssueDisclosure.EngagedAndVisible },
            new FakeTimeProvider());
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        editContext.NotifyFieldChanged(description);
        Assert.Empty(editContext.GetValidationMessages(description));

        using var registration = engine.Registry.Register(description);
        engine.OnRenderedFieldsChanged();

        Assert.Contains(engine.GetIssues(description), i => i.Severity == ValidationSeverity.Error);
        Assert.Contains(engine.GetVisibleIssues(), v => v.Field.Equals(description));
        Assert.True(engine.GetFieldState(description).HasErrors);
        Assert.NotEmpty(editContext.GetValidationMessages(description));
    }

    [Fact]
    public void An_override_forced_issue_still_shows_under_the_opt_in()
    {
        // The opt-in's visibility read is override-aware, per issue: a DisclosureOverride
        // answering true forces the issue visible from a field nothing renders, exactly as it
        // does on the submit channel.
        var order = new EngineOrder { Description = new string('x', 11) };
        var editContext = new EditContext(order);
        using var engine = Build(
            order, editContext,
            new FormidableOptions
            {
                LiveDisclosure = LiveIssueDisclosure.EngagedAndVisible,
                DisclosureOverride = _ => true,
            },
            new FakeTimeProvider());
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        editContext.NotifyFieldChanged(description);

        Assert.Contains(engine.GetIssues(description), i => i.Severity == ValidationSeverity.Error);
        Assert.Contains(engine.GetVisibleIssues(), v => v.Field.Equals(description));
        Assert.NotEmpty(editContext.GetValidationMessages(description));
    }

    [Fact]
    public async Task A_field_revealed_at_an_earlier_submit_still_counts_disclosed_after_leaving_the_page()
    {
        // Reveal is field-granular and merges by union, and the views carry no registration
        // filter for a revealed field — so a field revealed at submit #1 that has left the page
        // by submit #2 still discloses: message present, listed in the outcome summary, and not
        // reported suppressed. The mutation: re-deciding visibility at submit #2 would suppress
        // it and report it so.
        var suppressed = new List<ValidationIssue>();
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(
            order, editContext,
            new FormidableOptions { SuppressedIssueDiagnostic = suppressed.Add },
            new FakeTimeProvider());
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        var registration = engine.Registry.Register(description);
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.NotEmpty(editContext.GetValidationMessages(description));

        // The field leaves the page between submits; no override keeps it visible.
        registration.Dispose();
        engine.OnRenderedFieldsChanged();
        suppressed.Clear();

        var outcome = await engine.ValidateForSubmitAsync();

        Assert.False(outcome.CanProceed);
        Assert.NotEmpty(editContext.GetValidationMessages(description));
        Assert.Contains(engine.GetIssues(description), i => i.Severity == ValidationSeverity.Error);
        Assert.Contains("Order description", outcome.VisibleErrorSummary);
        Assert.DoesNotContain(suppressed, i => i.Path == nameof(EngineOrder.Description));
    }

    [Fact]
    public async Task A_field_revealed_by_one_issue_discloses_its_other_issues_and_reports_none_suppressed()
    {
        // Reveal is field-granular: two rules fail the same unregistered field, and a disclosure
        // override answers yes for one and no for the other. The yes reveals the FIELD, and a
        // revealed field's client submit answer discloses whole — so both messages show, and the
        // suppression diagnostic must agree with the views: an issue that is on screen is not
        // suppressed, however its own override answer came back.
        var suppressed = new List<ValidationIssue>();
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new DeclarationOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions
            {
                DisclosureOverride = issue => issue.Message switch
                {
                    "Description is required" => (bool?)false,
                    "Description is too short" => true,
                    _ => null,
                },
                SuppressedIssueDiagnostic = suppressed.Add,
            },
            new FakeTimeProvider());
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        var issues = engine.GetIssues(description);
        Assert.Contains(issues, i => i.Message == "Description is required");
        Assert.Contains(issues, i => i.Message == "Description is too short");
        Assert.Contains(engine.GetVisibleIssues(), v => v.Issue.Message == "Description is required");
        var messages = editContext.GetValidationMessages(description).ToList();
        Assert.Contains("Description is required", messages);
        Assert.Contains("Description is too short", messages);
        Assert.Empty(suppressed);
    }

    [Fact]
    public async Task A_server_error_on_a_suppressed_field_also_reveals_the_clients_own_error_at_once()
    {
        // Field-granular reveal, read through the ledger at apply time: a server-declared error
        // bypasses registration AND reveals the field, so the client's own suppressed error for
        // it discloses in the same apply — no refresh in between. The server's message differs
        // from the client's, so the two copies are tellable apart.
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions(), new FakeTimeProvider());
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var customer = new FieldIdentifier(order, nameof(EngineOrder.Customer));
        using var descReg = engine.Registry.Register(description);

        // The customer's client error is suppressed at submit: the field never registered.
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Empty(editContext.GetValidationMessages(customer));

        engine.ApplyServerIssues([new ValidationIssue("Customer", "Server needs a customer")]);

        var messages = editContext.GetValidationMessages(customer).ToList();
        Assert.Contains("Server needs a customer", messages);
        Assert.Equal(2, messages.Count);
        Assert.Equal(2, engine.GetIssues(customer).Count);
    }

    [Fact]
    public async Task A_submit_replaces_the_server_verdict()
    {
        // The server verdict is a snapshot of a round trip. A new submit produces a newer
        // whole-model answer, so the snapshot clears with it — only what the client's own rules
        // still say survives the pass.
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(order, editContext, new FormidableOptions(), time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var descReg = engine.Registry.Register(description);

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        engine.ApplyServerIssues([new ValidationIssue("Description", "Server rejected this description")]);
        Assert.Contains("Server rejected this description", editContext.GetValidationMessages(description));

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        var messages = editContext.GetValidationMessages(description).ToList();
        Assert.DoesNotContain("Server rejected this description", messages);
        Assert.NotEmpty(messages); // the client's own still-failing rule is what shows
    }

    [Fact]
    public async Task A_field_revealed_at_submit_clears_when_only_the_field_it_depends_on_is_engaged()
    {
        // The revealed field, Description, is never engaged here — only Customer.Name, the field
        // its submit rule reads, is. Submit blocks with Description's cross-field error disclosed;
        // fixing the name it depends on is the only committed change this test ever makes.
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new ChannelSeparatingValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions(),
            new FakeTimeProvider());
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var customerName = new FieldIdentifier(order.Customer, nameof(EngineCustomer.Name));
        using var descReg = engine.Registry.Register(description);

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Contains(engine.GetIssues(description), i => i.Message == "Description needs a named customer");

        order.Customer.Name = "Bo";
        editContext.NotifyFieldChanged(customerName);

        Assert.Empty(engine.GetIssues(description));
        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Field.Equals(description));
        Assert.Empty(editContext.GetValidationMessages(description));
    }

    private static FormValidationEngine<EngineOrder> Build(
        EngineOrder order,
        EditContext editContext,
        FormidableOptions options,
        TimeProvider time) =>
        new(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            options,
            time);
}
