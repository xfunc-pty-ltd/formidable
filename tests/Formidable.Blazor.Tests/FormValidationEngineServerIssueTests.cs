using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormValidationEngineServerIssueTests
{
    private readonly EngineOrder _order = new() { Description = "ok", Customer = new EngineCustomer() };
    private readonly EditContext _editContext;
    private readonly FakeTimeProvider _time = new();
    private readonly FormValidationEngine<EngineOrder> _engine;

    public FormValidationEngineServerIssueTests()
    {
        _editContext = new EditContext(_order);
        _engine = new FormValidationEngine<EngineOrder>(
            _order, _editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(), new FormidableOptions(), _time);
    }

    [Fact]
    public void Server_issues_land_inline_on_unregistered_fields_too()
    {
        _engine.ApplyServerIssues([new ValidationIssue("Description", "Server rejected this description")]);

        Assert.Contains(
            "Server rejected this description",
            _editContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public void Model_level_server_issue_lands_at_form_level()
    {
        _engine.ApplyServerIssues([new ValidationIssue(string.Empty, "Duplicate submission")]);

        Assert.Contains("Duplicate submission", _editContext.GetValidationMessages(new FieldIdentifier(_order, string.Empty)));
    }

    [Fact]
    public void Warning_severity_server_issues_do_not_block_or_write_messages()
    {
        _engine.ApplyServerIssues([new ValidationIssue("Description", "advisory", ValidationSeverity.Warning)]);

        Assert.Empty(_editContext.GetValidationMessages());
    }

    [Fact]
    public async Task Fixing_the_field_clears_the_server_issue_via_refresh()
    {
        _order.Description = string.Empty; // also fails client submit rules
        _engine.ApplyServerIssues([new ValidationIssue("Description", "Server rejected this description")]);
        Assert.NotEmpty(_editContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))));

        _order.Description = "ok"; // fix
        _editContext.NotifyFieldChanged(new FieldIdentifier(_order, nameof(EngineOrder.Description)));
        _time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        Assert.Empty(_editContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public void Disclosure_override_false_suppresses_server_issue()
    {
        using var engine = new FormValidationEngine<EngineOrder>(
            _order, new EditContext(_order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => false }, _time);

        engine.ApplyServerIssues([new ValidationIssue("Description", "suppressed")]);

        Assert.Empty(engine.EditContext.GetValidationMessages());
    }

    [Fact]
    public void Disclosure_override_true_keeps_server_issue()
    {
        using var engine = new FormValidationEngine<EngineOrder>(
            _order, new EditContext(_order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, _time);

        engine.ApplyServerIssues([new ValidationIssue("Description", "kept")]);

        Assert.Contains("kept", engine.EditContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))));
    }

    // A LINQ projection off a deserialized response body is the shape a caller actually holds, so
    // the parameter takes any sequence rather than charging a ToList() for the privilege. The
    // count matters as much as the acceptance: the payload is walked once, so an expensive or
    // single-pass sequence is safe to hand over.
    [Fact]
    public void A_lazy_sequence_is_accepted_and_walked_once()
    {
        var walks = 0;
        IEnumerable<ValidationIssue> Lazy()
        {
            walks++;
            yield return new ValidationIssue("Description", "Server rejected this description");
        }

        _engine.ApplyServerIssues(Lazy());

        Assert.Equal(1, walks);
        Assert.Contains(
            "Server rejected this description",
            _editContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public void Same_field_issues_replace_across_calls()
    {
        _engine.ApplyServerIssues([new ValidationIssue("Description", "first")]);
        _engine.ApplyServerIssues([new ValidationIssue("Description", "second")]);

        var messages = _engine.EditContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))).ToList();
        Assert.DoesNotContain("first", messages);
        Assert.Contains("second", messages);
    }

    [Fact]
    public void Applying_the_same_server_payload_twice_does_not_duplicate()
    {
        var payload = new[] { new ValidationIssue("Description", "Server rejected this description") };
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));

        _engine.ApplyServerIssues(payload);
        _engine.ApplyServerIssues(payload);

        Assert.Equal(1, _engine.GetIssues(description).Count(i => i.Message == "Server rejected this description"));
        Assert.Single(_editContext.GetValidationMessages(description));
    }

    [Fact]
    public void A_new_server_payload_replaces_the_previous_verdict()
    {
        var description = new FieldIdentifier(_order, nameof(EngineOrder.Description));
        var customer = new FieldIdentifier(_order, nameof(EngineOrder.Customer));

        _engine.ApplyServerIssues([new ValidationIssue("Description", "Description rejected")]);
        _engine.ApplyServerIssues([new ValidationIssue("Customer", "Customer rejected")]);

        Assert.Empty(_engine.GetIssues(description));
        Assert.Contains(_engine.GetIssues(customer), i => i.Message == "Customer rejected");
    }

    [Fact]
    public async Task Reapplied_server_issue_is_not_duplicated_by_an_intervening_refresh()
    {
        // Walkthrough repro: apply a server issue, edit the field (arms the debounced
        // refresh, whose client pass re-produces an equal issue and drops the applied-
        // server bookkeeping), let the refresh run, apply the same server verdict again.
        // Replace-per-apply must leave exactly ONE issue on the field.
        _order.Items = [new EngineItem(), new EngineItem()];
        var item0Sku = new FieldIdentifier(_order.Items[0], nameof(EngineItem.Sku));
        var item1Sku = new FieldIdentifier(_order.Items[1], nameof(EngineItem.Sku));

        // 1 & 2. Apply the server's first-submit verdict: both items are missing a SKU.
        _engine.ApplyServerIssues(
        [
            new ValidationIssue("Items[0].Sku", "SKU is required"),
            new ValidationIssue("Items[1].Sku", "SKU is required"),
        ]);

        // 3. Fix item 0 and let the debounced refresh (client-side, same message) run to
        //    completion before the next "send to server" click.
        _order.Items[0].Sku = "ABC";
        _editContext.NotifyFieldChanged(item0Sku);
        _time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        // 4. The server re-validates and re-sends its current verdict: only item 1 still fails.
        _engine.ApplyServerIssues([new ValidationIssue("Items[1].Sku", "SKU is required")]);

        // 5. Exactly one issue on the surviving field - no duplicate from the refresh.
        Assert.Equal(1, _engine.GetIssues(item1Sku).Count(i => i.Message == "SKU is required"));
        Assert.Single(_engine.GetVisibleIssues(), v => v.Field.Equals(item1Sku) && v.Issue.Message == "SKU is required");
    }

    [Fact]
    public async Task Client_submit_issues_survive_a_server_replace()
    {
        var order = new EngineOrder { Description = string.Empty, Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, _time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await engine.ValidateForSubmitAsync();
        var clientMessage = Assert.Single(editContext.GetValidationMessages(description));

        engine.ApplyServerIssues([new ValidationIssue("Description", "Server rejected this description")]);
        Assert.Contains("Server rejected this description", editContext.GetValidationMessages(description));

        engine.ApplyServerIssues([]);

        var messages = editContext.GetValidationMessages(description).ToList();
        Assert.Contains(clientMessage, messages);
        Assert.DoesNotContain("Server rejected this description", messages);
    }

    [Fact]
    public async Task Client_submit_issue_survives_a_server_replace_across_an_intervening_refresh()
    {
        // Same shape as Client_submit_issues_survive_a_server_replace, but with a debounced
        // refresh landing between the two ApplyServerIssues calls (an unrelated field's edit
        // arms it). The refresh re-derives Description's own still-failing client issue and
        // wipes the server-applied bookkeeping. A field-ownership-based re-keying of that
        // bookkeeping would wrongly adopt the client issue as "the server's" and let the second
        // (empty) apply delete it - exactly the regression this pin guards against.
        var order = new EngineOrder { Description = string.Empty, Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, _time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await engine.ValidateForSubmitAsync();
        var clientMessage = Assert.Single(editContext.GetValidationMessages(description));

        engine.ApplyServerIssues([new ValidationIssue("Description", "Server rejected this description")]);
        Assert.Contains("Server rejected this description", editContext.GetValidationMessages(description));

        // Unrelated field edit arms the debounced refresh; Description itself is untouched and
        // still fails its own client rule (still empty) the whole time.
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Customer)));
        _time.Advance(TimeSpan.FromMilliseconds(301));
        await Task.Yield();

        engine.ApplyServerIssues([]);

        var messages = editContext.GetValidationMessages(description).ToList();
        Assert.Contains(clientMessage, messages);
        Assert.DoesNotContain("Server rejected this description", messages);
    }
}
