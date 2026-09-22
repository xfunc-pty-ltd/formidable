using System.Linq.Expressions;
using Bunit;
using Bunit.Rendering;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

public class FormidableValidatorComponentTests : BunitContext
{
    public FormidableValidatorComponentTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    private IRenderedComponent<ContainerFragment> RenderForm(EngineOrder order, FormidableOptions? options = null) =>
        Render(builder =>
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, nameof(EditForm.Model), order);
            builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                if (options is not null)
                {
                    inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.Options), options);
                }
                inner.CloseComponent();
                inner.OpenComponent<ValidationSummary>(2);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

    private IRenderedComponent<AttachCollectionHost> RenderAttachCollection(
        EngineOrder order, bool keepRowsRegistered = false, FormidableOptions? options = null) =>
        Render<AttachCollectionHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.KeepRowsRegistered, keepRowsRegistered)
            .Add(p => p.Options, options));

    [Fact]
    public void Attaches_engine_and_shows_live_error_on_field_change()
    {
        var order = new EngineOrder { Description = new string('x', 11) };
        var cut = RenderForm(order, new FormidableOptions { DisclosureOverride = _ => true });

        var editContext = cut.FindComponent<EditForm>().Instance.EditContext!;
        cut.InvokeAsync(() => editContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Description))));

        cut.WaitForAssertion(() => Assert.Contains("10", cut.Markup));
    }

    [Fact]
    public void Cascades_form_context_to_descendants()
    {
        var order = new EngineOrder();
        FormidableFormContext? seen;
        Render(builder =>
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, nameof(EditForm.Model), order);
            builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.ChildContent), (RenderFragment)(ctx =>
                {
                    ctx.OpenComponent<ContextProbe>(0);
                    ctx.CloseComponent();
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        seen = ContextProbe.LastContext;

        Assert.NotNull(seen);
        Assert.Same(order, seen!.EditContext.Model);
        Assert.NotNull(seen.Registry);
        Assert.NotNull(seen.Engine);
    }

    // FormidableValidator's Engine/ApplyServerIssues forwarders exist so attach mode has the same
    // round-trip surface FormidableForm gives a page holding it with @ref — reaching through
    // Context.Engine should not be the only way to get there.
    [Fact]
    public async Task Engine_and_ApplyServerIssues_forwarders_mirror_FormidableForm()
    {
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var cut = RenderForm(order, new FormidableOptions { DisclosureOverride = _ => true });
        var validator = cut.FindComponent<FormidableValidator<EngineOrder>>();

        Assert.NotNull(validator.Instance.Engine);

        await cut.InvokeAsync(() => validator.Instance.ApplyServerIssues(
            [new ValidationIssue(nameof(EngineOrder.Description), "server says no")]));

        Assert.Contains(
            validator.Instance.Engine!.GetVisibleIssues(),
            v => v.Issue.Message == "server says no");
    }

    // Mirrors FormidableForm's identical guard test: both forwarders beat the engine's first
    // build the same way, and say so by name rather than a bare NullReferenceException.
    [Fact]
    public void Calls_before_the_engine_exists_name_the_component_and_the_reference()
    {
        var validator = new FormidableValidator<EngineOrder>();

        var byIssues = Assert.Throws<InvalidOperationException>(() => validator.ApplyServerIssues([]));
        var byProblem = Assert.Throws<InvalidOperationException>(
            () => validator.ApplyServerIssues(new FormidableValidationProblem()));

        Assert.Contains("FormidableValidator", byIssues.Message);
        Assert.Contains("@ref", byIssues.Message);
        Assert.Equal(byIssues.Message, byProblem.Message);
    }

    [Fact]
    public void Missing_cascading_edit_context_throws_clearly()
    {
        var exception = Assert.ThrowsAny<Exception>(() =>
            Render(builder =>
            {
                builder.OpenComponent<FormidableValidator<EngineOrder>>(0);
                builder.CloseComponent();
            }));

        Assert.Contains("EditForm", exception.Message);
    }

    // Attach mode has no OnAfterRenderAsync of its own to notice a removed row the way
    // FormidableForm's own render does — FormidableFormComponentTests's
    // Removing_a_row_after_a_submit_clears_its_summary_entry is the owned-mode analogue this
    // mirrors, down to the same validator, message and shortened RefreshDebounce. This is what
    // the FieldRegistry.Changed subscription exists to cover instead: draining it is what gets
    // OnRenderedFieldsChanged to run at all (it prunes the live channel and — since HasSubmitted
    // — arms the refresh), but a departed row's SUBMIT-time error is untouched by that prune; it
    // clears only once the refresh actually re-validates against the model and finds the row's
    // rule no longer firing (see OnRenderedFieldsChanged's own remarks). The refresh is a real
    // timer independent of the renderer's dispatch queue, so only real time — not another
    // drain — can wait for it.
    [Fact]
    public async Task Removing_a_row_in_attach_mode_clears_its_issues()
    {
        var keep = new EngineItem { Sku = "keep" };
        var doomed = new EngineItem();
        var order = new EngineOrder { Customer = new EngineCustomer(), Items = [keep, doomed] };

        var host = RenderAttachCollection(
            order, options: new FormidableOptions { RefreshDebounce = TimeSpan.FromMilliseconds(20) });
        var validator = host.FindComponent<FormidableValidator<EngineOrder>>();

        // Attach mode owns the engine, not the form, so submitting here means calling the engine
        // directly — the same call a page's own EditForm.OnSubmit handler would make — rather
        // than triggering the consumer's <form>, which this test does not even render a button
        // for.
        await host.InvokeAsync(() => validator.Instance.Engine!.ValidateForSubmitAsync());

        var doomedField = new FieldIdentifier(doomed, nameof(EngineItem.Sku));
        Assert.Contains(
            validator.Instance.Engine!.GetVisibleIssues(), v => v.Field.Equals(doomedField));

        // Exactly what a page's own Remove button does, and nothing more: the model is mutated
        // and the page re-renders. Nothing announces the change beyond the anchor's own Dispose —
        // the engine has to notice the row left by itself.
        order.Items.Remove(doomed);
        host.Render(parameters => parameters.Add(p => p.Order, order));

        // FieldRegistry.Changed fired synchronously from the removed anchor's Dispose, mid-batch;
        // the subscription captured the renderer's own synchronization context and posted a
        // continuation to it rather than acting inline. Enqueued behind that continuation on the
        // same context, this round trip does not return until the continuation — and the
        // reconcile it runs, which arms the refresh below — already has.
        await host.InvokeAsync(() => { });

        // Bounded polling rather than a bare delay: RefreshDebounce is only a lower bound on when
        // the refresh fires, and this keeps the test from being tied to exactly how much slower
        // than that the surrounding machinery happens to be.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline &&
            validator.Instance.Engine!.GetVisibleIssues().Any(v => v.Field.Equals(doomedField)))
        {
            await Task.Delay(20);
            await host.InvokeAsync(() => { });
        }

        Assert.DoesNotContain(
            validator.Instance.Engine!.GetVisibleIssues(), v => v.Field.Equals(doomedField));
    }

    [Fact]
    public async Task Kept_registered_fields_survive_attach_mode_changes()
    {
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, RowLiveValidator>();

        var kept = new EngineItem();
        var order = new EngineOrder { Customer = new EngineCustomer(), Items = [kept] };

        var host = RenderAttachCollection(order, keepRowsRegistered: true);
        var validator = host.FindComponent<FormidableValidator<EngineOrder>>();
        var editContext = host.FindComponent<EditForm>().Instance.EditContext!;
        var keptField = new FieldIdentifier(kept, nameof(EngineItem.Sku));

        await host.InvokeAsync(() => editContext.NotifyFieldChanged(keptField));
        host.WaitForAssertion(() => Assert.Contains(
            validator.Instance.Engine!.GetVisibleIssues(), v => v.Field.Equals(keptField)));

        // The row leaves the model; its anchor was rendered with KeepRegistered, so its Dispose
        // unregisters with that flag set and the registry keeps the field revealed even though
        // nothing renders it any more. The reconcile this wires up must respect that — pruning on
        // raw registration counts alone would drop a still-kept field's issue right along with a
        // genuinely departed one.
        order.Items.Remove(kept);
        host.Render(parameters => parameters.Add(p => p.Order, order));
        await host.InvokeAsync(() => { });

        Assert.Contains(
            validator.Instance.Engine!.GetVisibleIssues(), v => v.Field.Equals(keptField));
    }

    [Fact]
    public async Task A_batch_of_disposals_reconciles_once()
    {
        var kept = new EngineItem { Sku = "keep" };
        var doomed1 = new EngineItem();
        var doomed2 = new EngineItem();
        var doomed3 = new EngineItem();
        var order = new EngineOrder { Items = [kept, doomed1, doomed2, doomed3] };

        var host = RenderAttachCollection(order);
        var validator = host.FindComponent<FormidableValidator<EngineOrder>>();

        // The four initial registrations are their own batch, with their own single reconcile —
        // drained and its count captured as a baseline before the batch under test, so that
        // reconcile (needed regardless, or the version gate would never see a mismatch below)
        // does not inflate what this test is actually pinning.
        await host.InvokeAsync(() => { });
        var baseline = validator.Instance.ReconcileCount;

        // All three removed in the SAME render: each disposing anchor's Unregister fires
        // FieldRegistry.Changed and posts its own continuation to the same captured
        // synchronization context, in the order the diff disposes them.
        order.Items.Remove(doomed1);
        order.Items.Remove(doomed2);
        order.Items.Remove(doomed3);
        host.Render(parameters => parameters.Add(p => p.Order, order));

        // Enqueued behind all three posted continuations on that same synchronization context —
        // by the time this round trip completes, every one of them has already run: the first to
        // run did the reconcile and latched Registry.Version, and the other two found it
        // unchanged and skipped. A settled count read straight after, rather than through
        // WaitForAssertion's render-driven polling (nothing here re-renders on engine state
        // changes for it to catch), is deliberate: a dropped version gate would let the count
        // climb to baseline + 3 instead of stopping at baseline + 1.
        await host.InvokeAsync(() => { });

        Assert.Equal(baseline + 1, validator.Instance.ReconcileCount);
    }

    // Calls NotifyFieldSetChanged ahead of the posted continuation the automatic
    // FieldRegistry.Changed subscription queues from the removed anchor's Dispose — the exact
    // scenario the method's XML docs describe (reading Engine synchronously right after a
    // mutation that also unregisters a field, rather than waiting on the automatic reconcile's
    // posted continuation) — with the automatic subscription left live throughout rather than
    // disposed out of the way. The removal's render and the NotifyFieldSetChanged call are
    // dispatched together, in the SAME InvokeAsync callback, rather than as two separate
    // statements: a continuation posted mid-callback cannot be processed until the callback that
    // posted it returns, so doing both in one callback is what makes "ahead of it" true by
    // construction instead of by how fast either happens to run — two separate dispatched calls
    // observably race in practice, since the renderer's own posted continuations do not wait for
    // the poster's thread to go idle before a queued worker picks them up.
    [Fact]
    public async Task NotifyFieldSetChanged_reconciles_on_demand()
    {
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, RowLiveValidator>();

        var kept = new EngineItem { Sku = "keep" };
        var doomed = new EngineItem();
        var order = new EngineOrder { Customer = new EngineCustomer(), Items = [kept, doomed] };

        var host = RenderAttachCollection(order);
        var validator = host.FindComponent<FormidableValidator<EngineOrder>>();
        var editContext = host.FindComponent<EditForm>().Instance.EditContext!;
        var doomedField = new FieldIdentifier(doomed, nameof(EngineItem.Sku));

        await host.InvokeAsync(() => editContext.NotifyFieldChanged(doomedField));
        host.WaitForAssertion(() => Assert.Contains(
            validator.Instance.Engine!.GetVisibleIssues(), v => v.Field.Equals(doomedField)));

        await host.InvokeAsync(() =>
        {
            order.Items.Remove(doomed);
            host.Render(parameters => parameters.Add(p => p.Order, order));
            validator.Instance.NotifyFieldSetChanged();
        });

        Assert.DoesNotContain(
            validator.Instance.Engine!.GetVisibleIssues(), v => v.Field.Equals(doomedField));
    }

    // A row's anchor can unregister — posting a continuation while the validator's subscription
    // is still live — and the validator itself can dispose before that continuation is ever
    // processed: an edit right before navigating away reaches this shape, since posted
    // continuations are not guaranteed to drain before the next thing the consumer does. The
    // render that unregisters the row and the validator's own Dispose run inside the SAME
    // dispatched callback here, which is what makes "Dispose before the continuation is
    // processed" true by construction: a continuation posted mid-callback cannot begin
    // processing until the callback that posted it returns. A continuation processed after
    // Dispose would otherwise call OnRenderedFieldsChanged on the disposed engine; Dispose nulls
    // _engine, so ReconcileIfChanged's null guard makes it a no-op instead.
    [Fact]
    public async Task A_continuation_in_flight_at_dispose_does_not_reconcile_against_the_disposed_engine()
    {
        var kept = new EngineItem { Sku = "keep" };
        var doomed = new EngineItem();
        var order = new EngineOrder { Items = [kept, doomed] };

        var host = RenderAttachCollection(order);

        // Captured as a plain reference rather than read through host.FindComponent(...).Instance
        // each time: bUnit's own wrapper throws ComponentDisposedException once the component
        // leaves the render tree, but the underlying object — and ReconcileCount on it — is a
        // perfectly ordinary, still-readable .NET reference.
        var validator = host.FindComponent<FormidableValidator<EngineOrder>>().Instance;

        await host.InvokeAsync(() => { });
        var baseline = validator.ReconcileCount;

        order.Items.Remove(doomed);
        await host.InvokeAsync(() =>
        {
            host.Render(parameters => parameters.Add(p => p.Order, order));
            validator.Dispose();
        });

        // Drains the continuation the row's removal posted before the validator disposed.
        await host.InvokeAsync(() => { });

        Assert.Equal(baseline, validator.ReconcileCount);
    }

    // Blazor WebAssembly's default single-threaded runtime never installs a
    // SynchronizationContext on any thread, ever — OnFieldRegistryChanged's null-context branch
    // is that host's ONLY path, every time, not a rare fallback. Reproduced here by nulling
    // SynchronizationContext.Current for the extent of a direct Registry mutation (bypassing any
    // rendered component, so nothing else about the render pipeline is in play) and restoring it
    // immediately after — the same shape the host's own render dispatch would present on that
    // runtime. Two properties: the reconcile must NOT happen inline, synchronously, as part of
    // the call that fired FieldRegistry.Changed (checked immediately after that call returns,
    // still inside the same synchronous callback — nothing has had a chance to yield control back
    // to anything at that point), and it MUST still happen, eventually, once the thread pool
    // drains the continuation Task.Yield() queued.
    [Fact]
    public async Task Null_synchronization_context_defers_the_reconcile_through_the_thread_pool()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var host = RenderAttachCollection(order);
        var validator = host.FindComponent<FormidableValidator<EngineOrder>>().Instance;

        // Registered directly against the engine's own registry rather than through a rendered
        // anchor: its Dispose below is the only thing that needs to fire Changed for this test,
        // with no render/diff machinery around it to complicate which context is current.
        var phantomField = new FieldIdentifier(order, "Phantom");
        FieldRegistration? registration = null;
        await host.InvokeAsync(() => registration = validator.Engine!.Registry.Register(phantomField));
        await host.InvokeAsync(() => { });
        var baseline = validator.ReconcileCount;

        var reconcileCountImmediatelyAfterUnregister = -1;
        await host.InvokeAsync(() =>
        {
            var ambientContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                // Fires FieldRegistry.Changed with SynchronizationContext.Current == null —
                // OnFieldRegistryChanged's null-context branch, exactly as it always runs on
                // single-threaded WASM.
                registration!.Dispose();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(ambientContext);
            }

            reconcileCountImmediatelyAfterUnregister = validator.ReconcileCount;
        });

        Assert.Equal(baseline, reconcileCountImmediatelyAfterUnregister);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && validator.ReconcileCount == baseline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(baseline + 1, validator.ReconcileCount);
    }

    private sealed class ContextProbe : ComponentBase
    {
        public static FormidableFormContext? LastContext;

        [CascadingParameter]
        public FormidableFormContext? Context { get; set; }

        protected override void OnParametersSet() => LastContext = Context;
    }

    /// <summary>
    /// EditForm + FormidableValidator over a collection, rendered the way a page renders one: one
    /// keyed anchor per row, read from <see cref="Order"/> on every render, so removing a row
    /// from the list is all it takes to unregister that row. Mirrors
    /// FormidableFormComponentTests's own CollectionHost, for attach mode.
    /// </summary>
    private sealed class AttachCollectionHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool KeepRowsRegistered { get; set; }

        [Parameter]
        public FormidableOptions? Options { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, nameof(EditForm.Model), Order);
            builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                if (Options is not null)
                {
                    inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.Options), Options);
                }
                inner.AddComponentParameter(2, nameof(FormidableValidator<EngineOrder>.ChildContent), (RenderFragment)(ctx =>
                {
                    foreach (var item in Order.Items)
                    {
                        ctx.OpenComponent<FormidableFieldAnchor<string>>(0);
                        ctx.SetKey(item);
                        ctx.AddComponentParameter(
                            1, nameof(FormidableFieldAnchor<string>.For), (Expression<Func<string>>)(() => item.Sku));
                        if (KeepRowsRegistered)
                        {
                            ctx.AddComponentParameter(2, nameof(FormidableFieldAnchor<string>.KeepRegistered), true);
                        }
                        ctx.CloseComponent();
                    }
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>
    /// Draft validator requiring each item's SKU, unlike <see cref="EngineOrderValidator"/>
    /// (whose item rule lives only in its submit ruleset) — so a live pass triggered by
    /// <c>NotifyFieldChanged</c> alone, with no submit involved, puts a departed row's own issue
    /// on the live channel, where <c>OnRenderedFieldsChanged</c> prunes it synchronously.
    /// </summary>
    private sealed class RowLiveValidator : DraftSubmitValidator<EngineOrder>
    {
        protected override void ConfigureDraftRules() =>
            RuleForEach(x => x.Items).ChildRules(item =>
                item.RuleFor(i => i.Sku).NotEmpty().WithMessage("SKU is required"));

        protected override void ConfigureSubmitRules() =>
            RuleFor(x => x.Customer).NotNull();
    }
}
