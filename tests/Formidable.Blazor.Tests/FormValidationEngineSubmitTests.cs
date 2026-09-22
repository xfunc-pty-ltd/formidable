using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormValidationEngineSubmitTests
{
    private readonly EngineOrder _order = new();
    private readonly EditContext _editContext;
    private readonly FakeTimeProvider _time = new();
    private readonly FormValidationEngine<EngineOrder> _engine;
    private readonly FormidableOptions _options = new();

    public FormValidationEngineSubmitTests()
    {
        _editContext = new EditContext(_order);
        _engine = new FormValidationEngine<EngineOrder>(
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
        // engine so - no OnRenderedFieldsChanged, no further pass. RebuildStore's copy loops
        // re-play whatever the buckets already hold without re-checking current registration, so
        // a departed field's error persists in the store exactly as it arrived.
        descReg.Dispose();

        // Customer is required but was never registered at all, so its client submit error never
        // entered the bucket in the first place (suppressed by the registration gate at write
        // time). A server-declared error bypasses that gate outright - the one path through which
        // an unrendered field's blocking verdict still reaches the native store.
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

        using var custReg = _engine.Registry.Register(Field(_order, nameof(EngineOrder.Customer))); // revealed AFTER submit
        _order.Description = "ok";
        _editContext.NotifyFieldChanged(Field(_order, nameof(EngineOrder.Description)));
        _time.Advance(TimeSpan.FromMilliseconds(301));

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

    [Fact]
    public async Task Refresh_is_debounced()
    {
        using var descReg = _engine.Registry.Register(Field(_order, nameof(EngineOrder.Description)));
        await _engine.ValidateForSubmitAsync();

        _order.Description = "ok";
        _editContext.NotifyFieldChanged(Field(_order, nameof(EngineOrder.Description)));
        _time.Advance(TimeSpan.FromMilliseconds(150));
        Assert.NotEmpty(_editContext.GetValidationMessages(Field(_order, nameof(EngineOrder.Description)))); // not yet

        _editContext.NotifyFieldChanged(Field(_order, nameof(EngineOrder.Description))); // restarts window
        _time.Advance(TimeSpan.FromMilliseconds(150));
        Assert.NotEmpty(_editContext.GetValidationMessages(Field(_order, nameof(EngineOrder.Description)))); // still not

        _time.Advance(TimeSpan.FromMilliseconds(151));
        Assert.Empty(_editContext.GetValidationMessages(Field(_order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public async Task NormalizeOnSubmit_true_validates_the_normalized_model()
    {
        var model = new NormalizableOrder { Description = "  ok  " }; // 6 chars raw, 2 trimmed
        var editContext = new EditContext(model);
        using var engine = new FormValidationEngine<NormalizableOrder>(
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
        using var engine = new FormValidationEngine<NormalizableOrder>(
            model, editContext,
            new FluentValidationModelValidator<NormalizableOrder>(new NormalizableOrderValidator()),
            new ReflectionModelIntrospector(), new FormidableOptions(), _time);

        var outcome = await engine.ValidateForSubmitAsync();

        Assert.False(outcome.CanProceed); // raw value still exceeds MaximumLength(2)
        Assert.Equal("  ok  ", model.Description);
    }
}
