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
    public void Server_issues_merge_with_existing_submit_issues()
    {
        _engine.ApplyServerIssues([new ValidationIssue("Description", "first")]);
        _engine.ApplyServerIssues([new ValidationIssue("Customer", "second")]);

        Assert.NotEmpty(_editContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))));
        Assert.NotEmpty(_editContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Customer))));
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
    public void Same_field_issues_append_across_calls()
    {
        _engine.ApplyServerIssues([new ValidationIssue("Description", "first")]);
        _engine.ApplyServerIssues([new ValidationIssue("Description", "second")]);

        var messages = _engine.EditContext.GetValidationMessages(new FieldIdentifier(_order, nameof(EngineOrder.Description))).ToList();
        Assert.Contains("first", messages);
        Assert.Contains("second", messages);
    }
}
