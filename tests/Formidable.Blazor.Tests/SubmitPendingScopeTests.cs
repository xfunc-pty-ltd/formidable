using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// While a submit runs, the pending class sits on the fields the submit profile declares a rule
/// for: a declared field and a row's declared member wear it, and a rendered field with no rule
/// does not. A validator that cannot report its rules keeps every field pending. Each state is
/// read through both class routes, the kit's (FormidableCss over GetFieldState) and the native
/// inputs' (the EditContext's class provider).
/// </summary>
public class SubmitPendingScopeTests
{
    [Fact]
    public async Task A_field_with_no_submit_rule_is_not_pending_during_a_submit()
    {
        var order = NewOrder();
        var validator = new OrderValidator();
        var editContext = new EditContext(order);
        using var engine = NewEngine(order, editContext, new FluentValidationModelValidator<Order>(validator));
        var notes = new FieldIdentifier(order, nameof(Order.Notes));
        using var registration = engine.Registry.Register(notes);

        var submit = engine.ValidateForSubmitAsync();

        Assert.True(engine.IsSubmitting);
        Assert.False(engine.GetFieldState(notes).IsValidating);
        Assert.False(WearsPending(engine, editContext, notes));

        await LandAsync(engine, validator.Gate, submit);

        Assert.False(WearsPending(engine, editContext, notes));
    }

    [Fact]
    public async Task A_declared_field_is_pending_during_a_submit()
    {
        var order = NewOrder();
        var validator = new OrderValidator();
        var editContext = new EditContext(order);
        using var engine = NewEngine(order, editContext, new FluentValidationModelValidator<Order>(validator));
        var title = new FieldIdentifier(order, nameof(Order.Title));
        var sku = new FieldIdentifier(order.Lines[0], nameof(Line.Sku));
        using var titleRegistration = engine.Registry.Register(title);
        using var skuRegistration = engine.Registry.Register(sku);

        var submit = engine.ValidateForSubmitAsync();

        Assert.True(engine.IsSubmitting);
        Assert.True(engine.GetFieldState(title).IsValidating);
        Assert.True(WearsPending(engine, editContext, title));
        Assert.True(engine.GetFieldState(sku).IsValidating); // declared through RuleForEach, one per row
        Assert.True(WearsPending(engine, editContext, sku));

        await LandAsync(engine, validator.Gate, submit);

        Assert.False(WearsPending(engine, editContext, title));
        Assert.False(WearsPending(engine, editContext, sku));
    }

    [Fact]
    public async Task A_non_inspecting_validator_keeps_every_field_pending()
    {
        // A decorator that does not derive from DelegatingModelValidator hides the inner
        // validator's rules, so the form cannot tell which fields the submit checks.
        var order = NewOrder();
        var validator = new OrderValidator();
        var editContext = new EditContext(order);
        using var engine = NewEngine(
            order, editContext,
            new CountingValidator<Order>(new FluentValidationModelValidator<Order>(validator)));
        var title = new FieldIdentifier(order, nameof(Order.Title));
        var notes = new FieldIdentifier(order, nameof(Order.Notes));
        using var titleRegistration = engine.Registry.Register(title);
        using var notesRegistration = engine.Registry.Register(notes);

        var submit = engine.ValidateForSubmitAsync();

        Assert.True(engine.IsSubmitting);
        Assert.True(WearsPending(engine, editContext, title));
        Assert.True(engine.GetFieldState(notes).IsValidating);
        Assert.True(WearsPending(engine, editContext, notes));

        await LandAsync(engine, validator.Gate, submit);

        Assert.False(WearsPending(engine, editContext, title));
        Assert.False(WearsPending(engine, editContext, notes));
    }

    private static Order NewOrder() => new() { Lines = [new Line { Sku = "A-1" }] };

    private static FormidableEngine<Order> NewEngine(Order order, EditContext editContext, IModelValidator<Order> validator) =>
        new(order, editContext, validator, new ReflectionModelIntrospector(), new FormidableOptions(), new FakeTimeProvider());

    /// <summary>Whether the field wears the pending class, asserting first that the kit's class and the native provider's agree.</summary>
    private static bool WearsPending(FormidableEngine<Order> engine, EditContext editContext, FieldIdentifier field)
    {
        var pending = engine.Options.CssClasses.Pending;
        var kit = FormidableCss.Compute(engine.GetFieldState(field), engine.Options.CssClasses).Split(' ').Contains(pending);
        var native = editContext.FieldCssClass(field).Split(' ').Contains(pending);
        Assert.Equal(kit, native);
        return kit;
    }

    /// <summary>Releases the submit's gated rule and waits for the submit's outcome and for the publish that ends it.</summary>
    private static async Task LandAsync(FormidableEngine<Order> engine, TaskCompletionSource gate, Task<SubmitOutcome> submit)
    {
        var ended = new TaskCompletionSource();
        engine.StateChanged += (_, _) =>
        {
            if (!engine.IsValidating)
            {
                ended.TrySetResult();
            }
        };

        gate.SetResult();
        await submit.WaitAsync(TimeSpan.FromSeconds(5));
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(engine.IsValidating);
    }

    public sealed class Order
    {
        public string Title { get; set; } = "Order";

        public string Notes { get; set; } = string.Empty;

        public List<Line> Lines { get; set; } = [];
    }

    public sealed class Line
    {
        public string Sku { get; set; } = string.Empty;
    }

    /// <summary>A submit rule on Title that waits for <see cref="Gate"/>, a row rule on each line's Sku, and no rule on Notes.</summary>
    public sealed class OrderValidator : DraftSubmitValidator<Order>
    {
        public TaskCompletionSource Gate { get; } = new();

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(o => o.Title).MustAsync(async (_, ct) =>
            {
                await Gate.Task.WaitAsync(ct);
                return true;
            }).WithMessage("Title is checked");

            RuleForEach(o => o.Lines).ChildRules(line =>
                line.RuleFor(l => l.Sku).NotEmpty().WithMessage("SKU is required"));
        }
    }
}
