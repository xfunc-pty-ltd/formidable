using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormidableEngineSubmitTests
{
    private readonly EngineOrder _order = new();
    private readonly EditContext _editContext;
    private readonly FakeTimeProvider _time = new();
    private readonly FormidableEngine<EngineOrder> _engine;
    private readonly FormidableOptions _options = new();

    public FormidableEngineSubmitTests()
    {
        _editContext = new EditContext(_order);
        _engine = new FormidableEngine<EngineOrder>(
            _order, _editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(), _options, _time);
    }

    private FieldIdentifier Field(object owner, string name) => new(owner, name);

    [Fact]
    public async Task Submit_shows_only_revealed_fields_and_reports_their_display_names()
    {
        using var reg = _engine.Registry.Register(Field(_order, nameof(EngineOrder.Description)));
        // Customer is required but NOT registered -> unrevealed.

        var outcome = await _engine.ValidateForSubmitAsync();

        Assert.False(outcome.CanProceed);
        Assert.Equal(["Order description"], outcome.VisibleErrorSummary);
        Assert.NotEmpty(_editContext.GetValidationMessages(Field(_order, nameof(EngineOrder.Description))));
        Assert.Empty(_editContext.GetValidationMessages(Field(_order, nameof(EngineOrder.Customer))));
    }

    [Fact]
    public async Task Model_level_issues_are_always_visible()
    {
        _order.Description = "ok";
        _order.Customer = new EngineCustomer();
        _order.Items = [new() { Sku = "A" }, new() { Sku = "B" }, new() { Sku = "C" }, new() { Sku = "D" }];
        foreach (var item in _order.Items) { _engine.Registry.Register(Field(item, nameof(EngineItem.Sku))); }

        var outcome = await _engine.ValidateForSubmitAsync();

        Assert.False(outcome.CanProceed);
        Assert.Contains("No more than 3 items", _editContext.GetValidationMessages(new FieldIdentifier(_order, string.Empty)));
    }

    [Fact]
    public async Task Invalid_with_zero_visible_errors_blocks_with_form_level_message()
    {
        // Nothing registered: Description + Customer errors are all unrevealed.
        var outcome = await _engine.ValidateForSubmitAsync();

        Assert.False(outcome.CanProceed);
        var formLevel = _editContext.GetValidationMessages(new FieldIdentifier(_order, string.Empty)).ToList();
        Assert.Single(formLevel);
        Assert.Contains("not currently displayed", formLevel[0]);
        Assert.Single(outcome.VisibleErrorSummary);
    }

    [Fact]
    public async Task The_store_carries_errors_for_unrendered_fields()
    {
        using var descReg = _engine.Registry.Register(Field(_order, nameof(EngineOrder.Description)));

        var outcome = await _engine.ValidateForSubmitAsync();
        Assert.False(outcome.CanProceed);

        // Description answers for this submit and then leaves the page, but nothing tells the
        // engine so - no OnRenderedFieldsChanged, no further pass. The reveal ledger still
        // watches the field and the submit channel's source still holds its answer, and the view
        // re-plays what was disclosed without re-checking current registration - so a departed
        // field's error persists in the store exactly as it arrived. This is the bridge's stated
        // default: disclosure decided at the disclosure event, every surface answering alike.
        descReg.Dispose();

        // Customer is required but was never registered at all, so its client submit error was
        // suppressed at write time - the registration gate filters client errors before the
        // ledger ever reveals the field. A server-declared error bypasses that gate outright:
        // it reaches the native store whether or not the field ever rendered, and applying it
        // reveals the field, so the client's own error for it discloses alongside - and keeps
        // doing so until a passing submit or a reset clears the watch.
        _engine.ApplyServerIssues([new ValidationIssue(nameof(EngineOrder.Customer), "Customer is required")]);

        Assert.NotEmpty(_editContext.GetValidationMessages(Field(_order, nameof(EngineOrder.Description))));
        Assert.NotEmpty(_editContext.GetValidationMessages(Field(_order, nameof(EngineOrder.Customer))));
    }

    [Fact]
    public async Task Successful_submit_clears_all_messages_and_visible_state()
    {
        _order.Description = new string('x', 11);
        _editContext.NotifyFieldChanged(Field(_order, nameof(EngineOrder.Description))); // live error on screen
        _order.Description = "ok";
        _order.Customer = new EngineCustomer();

        var outcome = await _engine.ValidateForSubmitAsync();

        Assert.True(outcome.CanProceed);
        Assert.Empty(_editContext.GetValidationMessages());

        // The store is the native surface; the engine's own visible view is the one the kit reads,
        // and a submit that cleared one without the other would leave the message on screen.
        Assert.Empty(_engine.GetVisibleIssues());
    }

    [Fact]
    public async Task Refresh_clears_fixed_fields_and_keeps_unfixed_ones()
    {
        using var descReg = _engine.Registry.Register(Field(_order, nameof(EngineOrder.Description)));
        using var custReg = _engine.Registry.Register(Field(_order, nameof(EngineOrder.Customer)));
        await _engine.ValidateForSubmitAsync(); // both errors visible

        _order.Description = "ok"; // fix one
        _editContext.NotifyFieldChanged(Field(_order, nameof(EngineOrder.Description)));
        _time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.Empty(_editContext.GetValidationMessages(Field(_order, nameof(EngineOrder.Description))));
        Assert.NotEmpty(_editContext.GetValidationMessages(Field(_order, nameof(EngineOrder.Customer))));
    }

    [Fact]
    public async Task Refresh_does_not_surface_fields_not_seen_at_submit()
    {
        using var descReg = _engine.Registry.Register(Field(_order, nameof(EngineOrder.Description)));
        await _engine.ValidateForSubmitAsync(); // Customer unrevealed at submit

        // A server issue is the one thing the refresh clears that the live pass behind the same
        // edit leaves standing, so its departure is what says a refresh ran at all. Without it
        // Customer's silence would equally describe a refresh that never happened.
        var serverIssue = new ValidationIssue(
            nameof(EngineOrder.Description), "Server rejected this description");
        _engine.ApplyServerIssues([serverIssue]);
        Assert.Contains(
            serverIssue.Message,
            _editContext.GetValidationMessages(Field(_order, nameof(EngineOrder.Description))));

        using var custReg = _engine.Registry.Register(Field(_order, nameof(EngineOrder.Customer))); // revealed AFTER submit
        _order.Description = "ok";
        _editContext.NotifyFieldChanged(Field(_order, nameof(EngineOrder.Description)));
        _time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.DoesNotContain(
            serverIssue.Message,
            _editContext.GetValidationMessages(Field(_order, nameof(EngineOrder.Description))));

        // Customer stays quiet until the next submit even though it is now revealed and failing.
        Assert.Empty(_editContext.GetValidationMessages(Field(_order, nameof(EngineOrder.Customer))));
    }

    [Fact]
    public async Task Row_identity_survives_reorder()
    {
        var bad = new EngineItem { Sku = "" };
        var good = new EngineItem { Sku = "A" };
        _order.Description = "ok";
        _order.Customer = new EngineCustomer();
        _order.Items = [bad, good];
        _engine.Registry.Register(Field(bad, nameof(EngineItem.Sku)));
        _engine.Registry.Register(Field(good, nameof(EngineItem.Sku)));
        await _engine.ValidateForSubmitAsync();
        Assert.NotEmpty(_editContext.GetValidationMessages(Field(bad, nameof(EngineItem.Sku))));

        _order.Items.Reverse(); // bad row is now index 1
        _editContext.NotifyFieldChanged(Field(good, nameof(EngineItem.Sku)));
        _time.Advance(TimeSpan.FromMilliseconds(301));

        // The error follows the INSTANCE, not the index.
        Assert.NotEmpty(_editContext.GetValidationMessages(Field(bad, nameof(EngineItem.Sku))));
        Assert.Empty(_editContext.GetValidationMessages(Field(good, nameof(EngineItem.Sku))));
    }

    /// <summary>
    /// The refresh window slides on every committed change and fires once it settles. The
    /// observable is the SERVER source, which only a refresh clears: the client's own submit
    /// projection is rebuilt by the live pass each committed edit starts, so a client message
    /// cannot stand in for whether the refresh has fired.
    /// </summary>
    [Fact]
    public async Task Refresh_is_debounced()
    {
        var description = Field(_order, nameof(EngineOrder.Description));
        using var descReg = _engine.Registry.Register(description);
        await _engine.ValidateForSubmitAsync();

        _engine.ApplyServerIssues(
            [new ValidationIssue(nameof(EngineOrder.Description), "Reference already used", ValidationSeverity.Error)]);
        _order.Description = "ok";

        _editContext.NotifyFieldChanged(description);
        _time.Advance(TimeSpan.FromMilliseconds(150));
        Assert.Contains("Reference already used", _editContext.GetValidationMessages(description)); // not yet

        _editContext.NotifyFieldChanged(description); // restarts window
        _time.Advance(TimeSpan.FromMilliseconds(150));
        Assert.Contains("Reference already used", _editContext.GetValidationMessages(description)); // still not

        _time.Advance(TimeSpan.FromMilliseconds(151));
        Assert.Empty(_editContext.GetValidationMessages(description));
    }

    /// <summary>
    /// What the visitor sees when they correct a field a blocked submit revealed: the message
    /// goes with the live pass that edit starts, not with the debounced refresh behind it. The
    /// live channel runs the submit profile by default, so its report is the same whole-model
    /// submit-profile answer the refresh would produce — it rebuilds the submit channel's source
    /// itself rather than leaving a stale entry standing for a debounce.
    /// </summary>
    [Fact]
    public async Task A_corrected_field_clears_at_the_live_pass_not_at_the_refresh()
    {
        var description = Field(_order, nameof(EngineOrder.Description));
        using var descReg = _engine.Registry.Register(description);
        await _engine.ValidateForSubmitAsync();
        Assert.NotEmpty(_editContext.GetValidationMessages(description));

        _order.Description = "ok";
        _editContext.NotifyFieldChanged(description);

        // No time advanced: the window the refresh needs has not opened, and the message is gone.
        Assert.Empty(_editContext.GetValidationMessages(description));
    }

    /// <summary>
    /// A correction shape sharper than one that simply passes every rule: a value that clears
    /// the rule the submit reported and trips a different one shows the fresh message ALONE,
    /// never alongside the one it replaces.
    /// </summary>
    [Fact]
    public async Task A_correction_that_trips_a_second_rule_shows_only_the_fresh_message()
    {
        var description = Field(_order, nameof(EngineOrder.Description));
        using var descReg = _engine.Registry.Register(description);
        await _engine.ValidateForSubmitAsync();
        Assert.Equal(["'Order description' must not be empty."], _editContext.GetValidationMessages(description));

        _order.Description = "0123456789abc"; // clears NotEmpty, trips MaximumLength(10)
        _editContext.NotifyFieldChanged(description);

        var shown = _editContext.GetValidationMessages(description).ToList();
        Assert.Single(shown);
        Assert.Contains("10 characters or fewer", shown[0]);
    }

    /// <summary>
    /// The rebuild is keyed to the live channel running the submit profile ITSELF: a narrowed
    /// <see cref="FormidableOptions.LiveProfile"/> runs a different rule selection, whose report
    /// cannot stand in for a submit-profile answer, so the stale message stands until the
    /// debounced refresh answers under the submit profile itself.
    /// </summary>
    [Fact]
    public async Task A_narrowed_live_profile_leaves_the_stale_message_for_the_refresh()
    {
        _options.LiveProfile = ValidationProfile.Draft;
        var description = Field(_order, nameof(EngineOrder.Description));
        using var descReg = _engine.Registry.Register(description);
        await _engine.ValidateForSubmitAsync();
        Assert.NotEmpty(_editContext.GetValidationMessages(description));

        _order.Description = "ok";
        _editContext.NotifyFieldChanged(description);

        // No time advanced: the live pass this edit starts ran only the Draft bucket, which has
        // nothing to say about an empty Description, so the submit projection is untouched.
        Assert.NotEmpty(_editContext.GetValidationMessages(description));

        _time.Advance(TimeSpan.FromMilliseconds(301));
        Assert.Empty(_editContext.GetValidationMessages(description));
    }

    /// <summary>
    /// The rebuild covers advisories the same way it covers errors: a revealed field carrying a
    /// warning that no longer applies loses it at the live pass rather than standing until the
    /// refresh — the same lifetime symmetry warnings and errors already share at submit.
    /// </summary>
    [Fact]
    public async Task A_corrected_warning_clears_at_the_live_pass_not_at_the_refresh()
    {
        var description = Field(_order, nameof(EngineOrder.Description));
        using var descReg = _engine.Registry.Register(description);
        _order.Description = "a-b"; // passes NotEmpty, trips the warning-severity no-hyphen rule
        _order.Customer = new EngineCustomer();
        await _engine.ValidateForSubmitAsync();
        Assert.Contains(_engine.GetIssues(description), i => i.Message == "Avoid hyphens");

        _order.Description = "ab"; // clears the hyphen
        _editContext.NotifyFieldChanged(description);

        // No time advanced: the warning is gone before the refresh could ever fire.
        Assert.Empty(_engine.GetIssues(description));
    }

    [Fact]
    public async Task NormalizeOnSubmit_true_validates_the_normalized_model()
    {
        var model = new NormalizableOrder { Description = "  ok  " }; // 6 chars raw, 2 trimmed
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<NormalizableOrder>(
            model, editContext,
            new FluentValidationModelValidator<NormalizableOrder>(new NormalizableOrderValidator()),
            new ReflectionModelIntrospector(), new FormidableOptions { NormalizeOnSubmit = true }, _time);

        var outcome = await engine.ValidateForSubmitAsync();

        Assert.True(outcome.CanProceed); // MaximumLength(2) fails raw, passes trimmed
        Assert.Equal("ok", model.Description);
    }

    [Fact]
    public async Task NormalizeOnSubmit_defaults_false_and_leaves_the_model_untouched()
    {
        var model = new NormalizableOrder { Description = "  ok  " };
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<NormalizableOrder>(
            model, editContext,
            new FluentValidationModelValidator<NormalizableOrder>(new NormalizableOrderValidator()),
            new ReflectionModelIntrospector(), new FormidableOptions(), _time);

        var outcome = await engine.ValidateForSubmitAsync();

        Assert.False(outcome.CanProceed); // raw value still exceeds MaximumLength(2)
        Assert.Equal("  ok  ", model.Description);
    }
}
