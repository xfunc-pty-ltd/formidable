using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static Formidable.Blazor.Tests.Fixtures.EngineTestSync;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A server reply and a whole-form pass meeting in either order. A pass already running when the
/// reply arrived, or a re-check armed before it, knows nothing the reply does not and leaves it
/// standing; a submit or load begun after the reply, or a re-check after a later change, replaces it.
/// </summary>
public class FormidableEngineServerApplyRaceTests
{
    [Fact]
    public async Task A_server_reply_survives_a_refresh_that_began_before_it()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var validator = new GatedValidator { ShouldPass = true };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(order, editContext, validator, time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        // A rendered-field-set change arms the refresh, and its fire begins the pass, which the
        // async rule then holds open.
        engine.OnRenderedFieldsChanged();
        time.Advance(TimeSpan.FromMilliseconds(301));
        Assert.True(engine.IsValidating, "the refresh should be held open by the async rule");

        engine.ApplyServerIssues([new ValidationIssue("Description", "Server says no")]);
        Assert.Equal(["Server says no"], editContext.GetValidationMessages(description));

        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;

        Assert.Equal(["Server says no"], editContext.GetValidationMessages(description));
    }

    [Fact]
    public async Task A_refresh_a_server_reply_overtook_still_leaves_the_engine_idle()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var validator = new CancellationIgnoringValidator { ShouldPass = true };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(order, editContext, validator, time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        engine.OnRenderedFieldsChanged();
        time.Advance(TimeSpan.FromMilliseconds(301));
        Assert.True(engine.IsValidating, "the refresh should be held open by the async rule");

        engine.ApplyServerIssues([new ValidationIssue("Description", "Server says no")]);

        // The rule ignores its token, so even a pass the apply tried to cancel would run to its
        // end. Quiescence throws if the engine never reports itself idle again.
        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;

        Assert.False(engine.IsValidating);
        Assert.False(engine.GetFieldState(description).IsValidating);
    }

    [Fact]
    public async Task A_debounced_live_check_still_runs_after_a_reply_overtook_a_refresh()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var validator = new CancellationIgnoringValidator { ShouldPass = true };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(
            order, editContext, validator, time,
            new FormidableOptions { LiveDebounce = TimeSpan.FromMilliseconds(100) });
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        engine.OnRenderedFieldsChanged();
        time.Advance(TimeSpan.FromMilliseconds(301));
        Assert.True(engine.IsValidating, "the refresh should be held open by the async rule");

        engine.ApplyServerIssues([new ValidationIssue("Customer", "Server says no")]);
        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;

        // The gate is already open, so every later run of the rule answers at once, and now
        // answers no.
        validator.ShouldPass = false;
        order.Description = "edited";
        editContext.NotifyFieldChanged(description);
        time.Advance(TimeSpan.FromMilliseconds(101)); // the window closes and its check runs at once

        Assert.Contains(engine.GetIssues(description), i => i.Message == "the abandoned pass says no");
    }

    [Fact]
    public async Task A_server_reply_to_a_save_made_inside_an_edits_refresh_window_survives_that_window()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(order, editContext, new EngineOrderValidator(), time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);

        // An edit after a submit arms the whole-form re-check behind the default debounce, and
        // the save that follows inside that window comes back rejected before it closes.
        order.Description = "changed";
        editContext.NotifyFieldChanged(description);
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);
        time.Advance(TimeSpan.FromMilliseconds(150));
        engine.ApplyServerIssues([new ValidationIssue("Description", "Server says no")]);

        time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal(["Server says no"], editContext.GetValidationMessages(description));
    }

    [Fact]
    public async Task A_submit_already_running_when_a_reply_arrives_answers_its_caller_and_leaves_the_reply()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var validator = new GatedValidator { ShouldPass = true };
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, validator, new FakeTimeProvider());
        var customer = new FieldIdentifier(order, nameof(EngineOrder.Customer));

        var submit = engine.ValidateForSubmitAsync();
        Assert.True(engine.IsValidating, "the submit should be held open by the async rule");

        engine.ApplyServerIssues([new ValidationIssue("Customer", "Server says no")]);
        validator.Gate.SetResult();
        var outcome = await submit;

        Assert.True(outcome.CanProceed);
        Assert.False(engine.IsValidating);
        Assert.Equal(["Server says no"], editContext.GetValidationMessages(customer));
    }

    [Fact]
    public async Task A_live_check_already_running_when_a_reply_arrives_still_lands_its_verdict()
    {
        var customer = new EngineCustomer();
        var order = new EngineOrder { Description = "ok", Customer = customer };
        var validator = new SlowLiveRuleValidator();
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, validator, new FakeTimeProvider());
        var customerName = new FieldIdentifier(customer, nameof(EngineCustomer.Name));
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        customer.Name = "far too long";
        editContext.NotifyFieldChanged(customerName);
        Assert.True(engine.IsValidating, "the live check should be held open by the async rule");

        engine.ApplyServerIssues([new ValidationIssue("Description", "Server says no")]);

        var settled = Quiescence(engine);
        validator.Gate.SetResult();
        await settled;

        Assert.Contains(engine.GetIssues(customerName), i => i.Message == "Customer name is too long");
        Assert.Equal(["Server says no"], editContext.GetValidationMessages(description));
    }

    [Fact]
    public async Task A_load_already_running_when_a_reply_arrives_leaves_the_reply()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var validator = new GatedValidator { ShouldPass = true };
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, validator, new FakeTimeProvider());
        var customer = new FieldIdentifier(order, nameof(EngineOrder.Customer));

        var load = engine.DiscloseLoadedValuesAsync();
        Assert.True(engine.IsValidating, "the load should be held open by the async rule");

        engine.ApplyServerIssues([new ValidationIssue("Customer", "Server says no")]);
        validator.Gate.SetResult();
        await load;

        Assert.Equal(["Server says no"], editContext.GetValidationMessages(customer));
    }

    [Fact]
    public async Task A_server_reply_gives_way_to_a_submit_begun_after_it()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new EngineOrderValidator(), new FakeTimeProvider());
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        engine.ApplyServerIssues([new ValidationIssue("Description", "Server says no")]);

        // Nothing changed in between: asking again is enough.
        Assert.True((await engine.ValidateForSubmitAsync()).CanProceed);

        Assert.Empty(editContext.GetValidationMessages(description));
    }

    [Fact]
    public void A_server_reply_gives_way_to_the_refresh_a_later_field_set_change_arms()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        var time = new FakeTimeProvider();
        using var engine = Build(order, editContext, new EngineOrderValidator(), time);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        engine.ApplyServerIssues([new ValidationIssue("Description", "Server says no")]);
        engine.OnRenderedFieldsChanged();
        time.Advance(TimeSpan.FromMilliseconds(301));

        Assert.Empty(editContext.GetValidationMessages(description));
    }

    [Fact]
    public async Task A_server_reply_gives_way_to_a_load_begun_after_it()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var editContext = new EditContext(order);
        using var engine = Build(order, editContext, new EngineOrderValidator(), new FakeTimeProvider());
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        engine.ApplyServerIssues([new ValidationIssue("Description", "Server says no")]);
        await engine.DiscloseLoadedValuesAsync();

        Assert.Empty(editContext.GetValidationMessages(description));
    }

    private static FormidableEngine<EngineOrder> Build(
        EngineOrder order,
        EditContext editContext,
        FluentValidation.IValidator<EngineOrder> validator,
        TimeProvider time,
        FormidableOptions? options = null) =>
        new(
            order, editContext,
            new FluentValidationModelValidator<EngineOrder>(validator),
            new ReflectionModelIntrospector(),
            options ?? new FormidableOptions(),
            time);
}

/// <summary>
/// The same meeting through the shipped root, with synchronous rules and the refresh the first
/// render arms: whichever order a host's scheduler gives the two, the reply shows.
/// </summary>
public class FormidableFormServerApplyOrderTests : BunitContext
{
    public FormidableFormServerApplyOrderTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task A_reply_applied_before_the_first_renders_refresh_fires_survives_it()
    {
        var clock = new FakeTimeProvider();
        Services.AddSingleton<TimeProvider>(clock);
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var cut = RenderForm(order);
        var engine = cut.Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await cut.InvokeAsync(() =>
            cut.Instance.ApplyServerIssues([new ValidationIssue("Description", "Server says no")]));
        Assert.Equal(["Server says no"], engine.EditContext.GetValidationMessages(description));

        // Off the renderer's context, as a pool thread's timer callback would be: the refresh
        // the first render armed fires now, after the apply.
        clock.Advance(TimeSpan.FromMilliseconds(301));

        // Synchronous rules: the refresh ran to its end inside the fire, so no pass was ever in
        // flight for the apply to meet.
        Assert.False(engine.IsValidating);
        Assert.Equal(["Server says no"], engine.EditContext.GetValidationMessages(description));
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderForm(EngineOrder order)
    {
        var container = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment<FormidableFormContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableInputText>(0);
                    inner.AddComponentParameter(1, "For", (Expression<Func<string?>>)(() => order.Description));
                    inner.AddComponentParameter(2, "Value", order.Description);
                    inner.AddComponentParameter(3, "ValueChanged",
                        EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty));
                    inner.CloseComponent();
                }));
            builder.CloseComponent();
        });

        return container.FindComponent<FormidableForm<EngineOrder>>();
    }
}
