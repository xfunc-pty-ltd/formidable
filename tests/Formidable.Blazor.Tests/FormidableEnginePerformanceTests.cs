using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins <see cref="FormidableEngine{TModel}.GetVisibleIssues"/>'s shadow-map behaviour: an
/// engaged, clean, nothing-visible field must cost nothing, and the map must key on the field
/// alongside the message, never the message alone. The map itself is one form-wide set built
/// whenever any filtered channel holds entries at all, so it exists in Pin 1's scenario too, with
/// nothing ever put into it. Pin 1 establishes the first property by allocation ratio — such a
/// field must cost the same whether it is one of twenty or one of four hundred. Pin 2 establishes
/// the second, deterministically: two different fields sharing identical message text must both
/// show, and a single field repeating a message across channels must still collapse to one.
/// </summary>
public class FormidableEnginePerformanceTests
{
    private static FormidableEngine<EngineOrder> Build(
        EngineOrder model,
        EditContext editContext,
        IValidator<EngineOrder> validator,
        FormidableOptions options,
        TimeProvider? time = null) =>
        new(
            model,
            editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            options,
            time ?? new FakeTimeProvider());

    /// <summary>
    /// An engine whose model holds <paramref name="itemCount"/> non-empty collection rows plus a
    /// non-empty description and a non-null customer, disclosed rather than typed — so every one
    /// of those fields is engaged and clean, and <c>GetVisibleIssues()</c> has nothing to show for
    /// any of them. Clean means nothing surfaces rather than that the model passes: the fixture
    /// validator's model-level <c>Items.Count &lt;= 3</c> rule fails at every size built here and
    /// nothing discloses it — a load engages only fields it can read a value from, a model-level
    /// failure carries no member name, and no submit runs to reveal it or arm the gate. A change
    /// to what a load discloses would fail the pins below for a reason unrelated to allocation.
    /// </summary>
    private static async Task<FormidableEngine<EngineOrder>> BuildEngagedCleanEngineAsync(int itemCount)
    {
        var order = new EngineOrder
        {
            Description = "ok",
            Customer = new EngineCustomer { Name = "ok" },
        };

        for (var i = 0; i < itemCount; i++)
        {
            order.Items.Add(new EngineItem { Sku = $"sku{i}" });
        }

        var editContext = new EditContext(order);
        var engine = Build(order, editContext, new EngineOrderValidator(), new FormidableOptions());

        await engine.DiscloseLoadedValuesAsync();

        return engine;
    }

    private static void Warm(FormidableEngine<EngineOrder> engine, int calls)
    {
        for (var i = 0; i < calls; i++)
        {
            _ = engine.GetVisibleIssues();
        }
    }

    private static long MeasureAllocatedBytesPerCall(FormidableEngine<EngineOrder> engine, int iterations)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            _ = engine.GetVisibleIssues();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
    }

    /// <summary>
    /// Mutation that must break this: remove the LIVE loop's
    /// <c>if (issues.Count == 0) { continue; }</c> guard in <c>GetVisibleIssues</c> — the
    /// ~20-vs-~400 ratio then measures roughly 16.7x, so the 2x bound below fails hard. The
    /// advisory loop's own copy of that guard is out of this pin's reach: this scenario carries
    /// no advisories, so <c>SubmitAdvisoryEntries()</c> yields nothing and that loop body, guard
    /// included, never runs — removing it leaves this pin passing.
    /// </summary>
    [Fact]
    public async Task GetVisibleIssues_allocation_is_flat_in_the_engaged_set()
    {
        using var small = await BuildEngagedCleanEngineAsync(itemCount: 18); // + Description + Customer ~= 20
        using var large = await BuildEngagedCleanEngineAsync(itemCount: 398); // + Description + Customer ~= 400

        Assert.Empty(small.GetVisibleIssues());
        Assert.Empty(large.GetVisibleIssues());

        const int warmup = 50;
        const int iterations = 100;

        Warm(small, warmup);
        Warm(large, warmup);

        var smallBytes = MeasureAllocatedBytesPerCall(small, iterations);
        var largeBytes = MeasureAllocatedBytesPerCall(large, iterations);

        Assert.True(
            largeBytes <= smallBytes * 2,
            $"expected the ~400-engaged engine to allocate no more than 2x the ~20-engaged one; " +
            $"small={smallBytes} B, large={largeBytes} B");
    }

    /// <summary>
    /// Two different fields whose submit rules fail with the SAME message text — the shape that
    /// makes the shadow map's field-keying reachable: only a key naming the field can tell
    /// "already showing for this field" from "already showing for some other field".
    /// </summary>
    private sealed class SharedMessageAcrossFieldsValidator : DraftSubmitValidator<EngineOrder>
    {
        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(x => x.Description).NotEmpty().WithMessage("Same message");
            RuleFor(x => x.Customer!.Name)
                .Must(name => name.Length <= 2)
                .WithMessage("Same message")
                .When(x => x.Customer is not null);
        }
    }

    /// <summary>
    /// Mutation that must break this: key <c>RecordShowing</c> and the whole-form
    /// <c>ExceptShadowed</c> on <c>issue.Message</c> alone, dropping the field from the key. Under
    /// that mutation the customer-name entry disappears, because the description field's
    /// submit-revealed "Same message" already occupies the (message-only) set.
    /// </summary>
    [Fact]
    public async Task GetVisibleIssues_keys_the_shadow_map_on_field_and_message_not_message_alone()
    {
        var order = new EngineOrder
        {
            Description = string.Empty,
            Customer = new EngineCustomer { Name = "ok" },
        };
        var editContext = new EditContext(order);
        var options = new FormidableOptions { DisclosureOverride = _ => true };
        using var engine = Build(order, editContext, new SharedMessageAcrossFieldsValidator(), options);

        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var customerName = new FieldIdentifier(order.Customer, nameof(EngineCustomer.Name));

        // Description fails and is revealed by a blocked submit; Customer.Name still passes, so
        // the submit reveals only Description.
        var outcome = await engine.ValidateForSubmitAsync();
        Assert.False(outcome.CanProceed);

        // Customer.Name is edited into failing the SAME message, live — a different field the
        // submit above never touched.
        order.Customer.Name = "too long now";
        editContext.NotifyFieldChanged(customerName);

        // Description is edited too, remaining empty, so its live verdict repeats the message
        // the submit already put on screen for it: the within-field half of the shadow rule,
        // which must still collapse to the one entry.
        editContext.NotifyFieldChanged(description);

        var visible = engine.GetVisibleIssues();

        Assert.Single(visible, v => v.Field.Equals(description) && v.Issue.Message == "Same message");
        Assert.Single(visible, v => v.Field.Equals(customerName) && v.Issue.Message == "Same message");
        Assert.Equal(2, visible.Count);
    }
}
