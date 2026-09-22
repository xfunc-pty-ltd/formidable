using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

public class FormValidationEngineHardeningTests
{
    [Fact]
    public async Task Edit_during_first_submit_gets_revalidated_after_submit_completes()
    {
        var order = new EngineOrder();
        var validator = new GatedValidator();
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, time);

        var submit = engine.ValidateForSubmitAsync();
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        time.Advance(TimeSpan.FromMilliseconds(301)); // debounce elapses while submit is in flight -> defer + re-arm

        validator.Gate.SetResult();
        await submit;
        Assert.Equal(1, validator.Started);

        time.Advance(TimeSpan.FromMilliseconds(301)); // the re-armed refresh now runs
        Assert.Equal(2, validator.Started);
    }

    // Pins the post-submit lifetime of advisories (warnings/info). At submit the engine captures
    // non-error issues for every VISIBLE field, which includes fields carrying no error at all.
    // The debounced refresh must keep refreshing those fields' advisories - filtering them by the
    // submit-visible ERROR set alone silently dropped a warning shown on an error-free field the
    // moment any other field changed.
    [Fact]
    public async Task Submit_warning_on_an_error_free_field_survives_the_debounced_refresh()
    {
        var order = new EngineOrder { Description = "a-b" }; // NotEmpty passes; the hyphen warning fires
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true }, time);

        // Customer is null, so submit records an error elsewhere and HasSubmitted stays true.
        await engine.ValidateForSubmitAsync();
        Assert.Contains(engine.GetIssues(description), i => i.Message == "Avoid hyphens");

        order.Customer = new EngineCustomer();
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Customer)));
        time.Advance(TimeSpan.FromMilliseconds(301)); // the debounced refresh pass runs
        await Task.Yield();

        Assert.Contains(engine.GetIssues(description), i => i.Message == "Avoid hyphens");
    }

    [Fact]
    public async Task ApplyServerIssues_clears_a_prior_fault()
    {
        var order = new EngineOrder();
        var validator = new ThrowingValidator();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            new FormidableOptions(), new FakeTimeProvider());

        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        await Task.Yield();
        Assert.NotEmpty(editContext.GetValidationMessages(new FieldIdentifier(order, string.Empty)));

        engine.ApplyServerIssues([new ValidationIssue("Description", "server said no")]);

        Assert.DoesNotContain(
            editContext.GetValidationMessages(new FieldIdentifier(order, string.Empty)),
            m => m.Contains("could not run to completion"));
        Assert.Contains("server said no",
            editContext.GetValidationMessages(new FieldIdentifier(order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public async Task Suppressed_issues_reach_the_diagnostic_callback()
    {
        var order = new EngineOrder();
        var suppressed = new List<ValidationIssue>();
        var editContext = new EditContext(order);
        using var engine = new FormValidationEngine<EngineOrder>(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { SuppressedIssueDiagnostic = suppressed.Add }, new FakeTimeProvider());
        using var reg = engine.Registry.Register(new FieldIdentifier(order, nameof(EngineOrder.Description)));

        await engine.ValidateForSubmitAsync();

        Assert.Contains(suppressed, i => i.Path == nameof(EngineOrder.Customer)); // unregistered -> suppressed
        Assert.DoesNotContain(suppressed, i => i.Path == nameof(EngineOrder.Description)); // registered -> visible
    }

    [Fact]
    public async Task Diagnostic_is_not_invoked_when_override_reveals_everything()
    {
        var order = new EngineOrder();
        var suppressed = new List<ValidationIssue>();
        using var engine = new FormValidationEngine<EngineOrder>(
            order, new EditContext(order),
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            new FormidableOptions { DisclosureOverride = _ => true, SuppressedIssueDiagnostic = suppressed.Add },
            new FakeTimeProvider());

        await engine.ValidateForSubmitAsync();

        Assert.Empty(suppressed);
    }
}
