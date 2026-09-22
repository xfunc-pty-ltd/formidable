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
}
