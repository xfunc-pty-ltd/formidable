using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the three sentences the library puts in front of a user on a rendered form: the defensive
/// gate's explanation, the name a model-level entry is listed under in a submit outcome, and what
/// a faulted pass says. Each is pinned from both ends — the shipped default word for word, so a
/// silent re-voicing cannot pass unnoticed, and a replacement reaching every surface the shipped
/// one reaches. Two also carry read-timing pins, because their options are documented as read at
/// different moments: the gate's at every read, the fault's where the fault is filed.
/// </summary>
public class ShippedTextOptionsTests : BunitContext
{
    private const string ShippedGateSentence =
        "The form cannot be submitted because information that is not currently displayed is invalid.";

    private const string ShippedModelLevelName = "This form";

    private const string ShippedFaultSentence =
        "Validation could not run to completion; recent changes may not be fully validated.";

    private static FormidableEngine<EngineOrder> Build(
        EngineOrder order,
        EditContext editContext,
        FormidableOptions options) =>
        new(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(new EngineOrderValidator()),
            new ReflectionModelIntrospector(),
            options,
            new FakeTimeProvider());

    private static FormidableEngine<EngineOrder> BuildFaulting(
        EngineOrder order,
        EditContext editContext,
        FormidableOptions options,
        ThrowingValidator validator) =>
        new(
            order,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            options,
            new FakeTimeProvider());

    /// <summary>An order whose only failing rule is the model-level item cap, with every field it
    /// does render registered — so a blocked submit discloses a model-level error rather than
    /// arming the gate.</summary>
    private static EngineOrder ModelLevelFailure(FormidableEngine<EngineOrder> engine, EngineOrder order)
    {
        order.Description = "ok";
        order.Customer = new EngineCustomer();
        order.Items = [new() { Sku = "A" }, new() { Sku = "B" }, new() { Sku = "C" }, new() { Sku = "D" }];
        foreach (var item in order.Items)
        {
            engine.Registry.Register(new FieldIdentifier(item, nameof(EngineItem.Sku)));
        }

        return order;
    }

    [Fact]
    public async Task The_gate_shows_the_sentence_the_library_ships_when_nothing_replaces_it()
    {
        // Nothing registered: every failing field is hidden, which is the case the gate speaks for.
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions());

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        var modelLevel = new FieldIdentifier(order, string.Empty);
        Assert.Equal(ShippedGateSentence, Assert.Single(engine.GetIssues(modelLevel)).Message);
        Assert.Equal(ShippedGateSentence, Assert.Single(editContext.GetValidationMessages(modelLevel)));
    }

    [Fact]
    public async Task The_summary_names_the_model_level_the_way_the_library_ships_it()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new FormidableOptions());

        // The gate's own arm: nothing was disclosed, so the one entry stands for the whole form.
        Assert.Equal([ShippedModelLevelName], (await engine.ValidateForSubmitAsync()).VisibleErrorSummary);

        // And the arm that names a disclosed error the validator raised against the model itself,
        // which reaches the same fallback by a different route: it has a display name and a path,
        // and both are empty.
        var second = new EngineOrder();
        var secondContext = new EditContext(second);
        using var secondEngine = Build(second, secondContext, new FormidableOptions());
        ModelLevelFailure(secondEngine, second);

        var outcome = await secondEngine.ValidateForSubmitAsync();
        Assert.False(outcome.CanProceed);
        Assert.Equal([ShippedModelLevelName], outcome.VisibleErrorSummary);
    }

    [Fact]
    public async Task A_replaced_gate_sentence_reaches_the_engine_reads_and_the_message_store()
    {
        const string replacement = "Bitte prüfen Sie die ausgeblendeten Angaben.";
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(
            order, editContext, new FormidableOptions { DefensiveGateMessage = replacement });

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);

        var modelLevel = new FieldIdentifier(order, string.Empty);
        Assert.Equal(replacement, Assert.Single(engine.GetIssues(modelLevel)).Message);
        Assert.Equal(
            replacement,
            Assert.Single(engine.GetVisibleIssues(), v => v.Field.Equals(modelLevel)).Issue.Message);
        Assert.Equal(replacement, Assert.Single(editContext.GetValidationMessages(modelLevel)));
        Assert.DoesNotContain(ShippedGateSentence, engine.GetIssues(modelLevel).Select(i => i.Message));
    }

    [Fact]
    public void A_replaced_gate_sentence_is_what_the_model_message_renders()
    {
        const string replacement = "Something you cannot see is holding this up.";
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();

        // Description and Customer both fail the submit profile and the page renders neither, so
        // the blocked submit has only the gate to show — through the one component built for it.
        var order = new EngineOrder();
        var options = new FormidableOptions { DefensiveGateMessage = replacement };
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "Options", options);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableModelMessage>(0);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();

        form.InvokeAsync(() => form.Instance.SubmitAsync());

        form.WaitForAssertion(() =>
        {
            var item = Assert.Single(form.FindAll("li.formidable-message--error"));
            Assert.Equal(replacement, item.TextContent);
        });
    }

    [Fact]
    public async Task A_replaced_model_level_name_is_what_a_submit_outcome_lists()
    {
        const string replacement = "Dieses Formular";
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = Build(
            order, editContext, new FormidableOptions { ModelLevelDisplayName = replacement });

        // The gate's arm.
        Assert.Equal([replacement], (await engine.ValidateForSubmitAsync()).VisibleErrorSummary);

        // The disclosed-error arm, which names the model level in its own separate expression.
        var second = new EngineOrder();
        var secondContext = new EditContext(second);
        using var secondEngine = Build(
            second, secondContext, new FormidableOptions { ModelLevelDisplayName = replacement });
        ModelLevelFailure(secondEngine, second);

        Assert.Equal([replacement], (await secondEngine.ValidateForSubmitAsync()).VisibleErrorSummary);
    }

    [Fact]
    public async Task The_gate_sentence_is_read_at_each_use_rather_than_held()
    {
        const string replacement = "The rest of the form disagrees.";
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var options = new FormidableOptions();
        using var engine = Build(order, editContext, options);
        var modelLevel = new FieldIdentifier(order, string.Empty);

        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Equal(ShippedGateSentence, Assert.Single(engine.GetIssues(modelLevel)).Message);

        // No pass, no notification, nothing but the property changing under a standing gate. The
        // engine's own reads answer from it immediately, because the gate is a predicate with no
        // filed entry and its issue is built where it is read.
        options.DefensiveGateMessage = replacement;
        Assert.Equal(replacement, Assert.Single(engine.GetIssues(modelLevel)).Message);
        Assert.Equal(
            replacement,
            Assert.Single(engine.GetVisibleIssues(), v => v.Field.Equals(modelLevel)).Issue.Message);

        // The message store is a projection of those reads rather than a read of them, so it is
        // one rebuild behind: it still holds what it was last given, and the next pass hands it
        // the replacement.
        Assert.Equal(ShippedGateSentence, Assert.Single(editContext.GetValidationMessages(modelLevel)));
        Assert.False((await engine.ValidateForSubmitAsync()).CanProceed);
        Assert.Equal(replacement, Assert.Single(editContext.GetValidationMessages(modelLevel)));
    }

    [Fact]
    public async Task A_fault_shows_the_sentence_the_library_ships_when_nothing_replaces_it()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = BuildFaulting(order, editContext, new FormidableOptions(), new ThrowingValidator());
        var modelLevel = new FieldIdentifier(order, string.Empty);

        // The draft rule throws, so the live pass this notification starts reports rather than
        // rethrows, and the fault issue is what the form is left holding.
        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        await Task.Yield();

        Assert.Equal(ShippedFaultSentence, Assert.Single(engine.GetIssues(modelLevel)).Message);
        Assert.Equal(ShippedFaultSentence, Assert.Single(editContext.GetValidationMessages(modelLevel)));
    }

    [Fact]
    public async Task A_replaced_fault_sentence_reaches_the_engine_reads_and_the_message_store()
    {
        const string replacement = "Wir konnten nicht alles prüfen.";
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        using var engine = BuildFaulting(
            order, editContext, new FormidableOptions { ValidationFaultMessage = replacement },
            new ThrowingValidator());
        var modelLevel = new FieldIdentifier(order, string.Empty);

        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        await Task.Yield();

        Assert.Equal(replacement, Assert.Single(engine.GetIssues(modelLevel)).Message);
        Assert.Equal(
            replacement,
            Assert.Single(engine.GetVisibleIssues(), v => v.Field.Equals(modelLevel)).Issue.Message);
        Assert.Equal(replacement, Assert.Single(editContext.GetValidationMessages(modelLevel)));
        Assert.DoesNotContain(ShippedFaultSentence, engine.GetIssues(modelLevel).Select(i => i.Message));
    }

    [Fact]
    public void A_replaced_fault_sentence_is_what_the_model_message_renders()
    {
        const string replacement = "Some of this could not be checked.";
        Services.AddFormidable();
        var validator = new FaultAndFieldErrorValidator();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>>(validator);

        var order = new EngineOrder { Description = "ok" };
        var options = new FormidableOptions { ValidationFaultMessage = replacement };
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "Options", options);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableModelMessage>(0);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();

        validator.Throw = true;
        form.InvokeAsync(() => form.Instance.Engine!.EditContext.NotifyFieldChanged(
            new FieldIdentifier(order, nameof(EngineOrder.Description))));

        form.WaitForAssertion(() =>
        {
            var item = Assert.Single(form.FindAll("li.formidable-message--error"));
            Assert.Equal(replacement, item.TextContent);
        });
    }

    [Fact]
    public async Task The_fault_sentence_is_read_where_the_fault_is_filed_rather_than_at_each_read()
    {
        const string replacement = "Some of this could not be checked.";
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var options = new FormidableOptions();
        var validator = new ThrowingValidator();
        using var engine = BuildFaulting(order, editContext, options, validator);
        var modelLevel = new FieldIdentifier(order, string.Empty);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        editContext.NotifyFieldChanged(description);
        await Task.Yield();
        Assert.Equal(ShippedFaultSentence, Assert.Single(engine.GetIssues(modelLevel)).Message);

        // The issue is filed, not derived, so the standing one keeps the sentence it was written
        // with however many times it is read afterwards.
        options.ValidationFaultMessage = replacement;
        Assert.Equal(ShippedFaultSentence, Assert.Single(engine.GetIssues(modelLevel)).Message);
        Assert.Equal(ShippedFaultSentence, Assert.Single(editContext.GetValidationMessages(modelLevel)));

        // A clean pass clears it, and the next fault is filed from the property as it now reads.
        validator.Throw = false;
        editContext.NotifyFieldChanged(description);
        await Task.Yield();
        Assert.Empty(engine.GetIssues(modelLevel));

        validator.Throw = true;
        editContext.NotifyFieldChanged(description);
        await Task.Yield();
        Assert.Equal(replacement, Assert.Single(engine.GetIssues(modelLevel)).Message);
    }

    [Fact]
    public async Task A_server_apply_ends_a_standing_fault_with_no_pass_between()
    {
        var order = new EngineOrder();
        var editContext = new EditContext(order);
        var validator = new ThrowingValidator();
        using var engine = BuildFaulting(order, editContext, new FormidableOptions(), validator);
        var modelLevel = new FieldIdentifier(order, string.Empty);

        editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description)));
        await Task.Yield();
        Assert.Equal(ShippedFaultSentence, Assert.Single(engine.GetIssues(modelLevel)).Message);

        // The rule is still throwing and no pass has run since, so nothing here is a recovery:
        // the apply is the second of the two places the fault issue is cleared from, and it needs
        // no pass at all. An empty payload keeps the assertion about the fault alone.
        engine.ApplyServerIssues([]);

        Assert.Empty(engine.GetIssues(modelLevel));
        Assert.Empty(editContext.GetValidationMessages(modelLevel));
    }
}
