using System.Linq.Expressions;
using Bunit;
using Bunit.Rendering;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Formidable.Blazor.Tests;

public class FormidableValidatorComponentTests : BunitContext
{
    public FormidableValidatorComponentTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    private IRenderedComponent<ContainerFragment> RenderForm(
        EngineOrder order,
        FormidableOptions? options = null,
        bool? focusFirstErrorOnInvalidSubmit = null,
        Func<FieldIdentifier, ValueTask<bool>>? focusFallback = null) =>
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
                if (focusFirstErrorOnInvalidSubmit is { } focusParameter)
                {
                    inner.AddComponentParameter(
                        2,
                        nameof(FormidableValidator<EngineOrder>.FocusFirstErrorOnInvalidSubmit),
                        focusParameter);
                }
                if (focusFallback is not null)
                {
                    inner.AddComponentParameter(
                        3, nameof(FormidableValidator<EngineOrder>.FocusFallback), focusFallback);
                }
                inner.CloseComponent();
                inner.OpenComponent<ValidationSummary>(4);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

    /// <summary>
    /// The focus seam the way both roots reach it: the shipped <c>FormidableFocusService</c> over
    /// bUnit's own recording JS module, so a test asserts on the interop call the browser would
    /// have made. <paramref name="focusLands"/> is what <c>focusField</c> answers — false is the
    /// miss a fallback exists to recover.
    /// </summary>
    private BunitJSModuleInterop SetUpFocusModule(bool focusLands = true)
    {
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(focusLands);
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
        return module;
    }

    /// <summary>
    /// The displaced-click guard's own module plan. <paramref name="rootFound"/> is what the
    /// script answers: attach mode renders no element of its own, so a page carrying neither the
    /// model-level gate id nor a <c>&lt;form&gt;</c> around a registered field leaves the guard
    /// nothing to scope itself to, and false is that answer.
    /// </summary>
    private BunitJSModuleInterop SetUpClickRecoveryModule(bool rootFound)
    {
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("registerClickRecovery", _ => true).SetResult(rootFound);
        module.SetupVoid("releaseClickRecovery", _ => true).SetVoidResult();
        return module;
    }

    // Degrade loudly, never silently. A root the guard cannot be scoped to is a page that keeps
    // the defect, and the one thing worse than keeping it is keeping it invisibly — so the miss
    // reports on the same dual channel an unwired FocusFallback's own miss already uses, naming
    // both routes that would close it.
    [Fact]
    public async Task An_attached_root_with_nothing_to_scope_the_guard_to_reports_a_diagnostic()
    {
        var loggerProvider = new CapturingLoggerProvider();
        Services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddProvider(loggerProvider)));
        SetUpClickRecoveryModule(rootFound: false);

        var cut = RenderForm(new EngineOrder());

        cut.WaitForAssertion(() => Assert.Contains(
            loggerProvider.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("displaced-click guard")));

        await Services.DisposeAsync();
    }

    // The control the pin above needs to mean anything: a root the script did find reports
    // nothing, so a regression that simply always warned would not read as a pass.
    [Fact]
    public async Task An_attached_root_the_guard_was_scoped_to_reports_nothing()
    {
        var loggerProvider = new CapturingLoggerProvider();
        Services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddProvider(loggerProvider)));
        var module = SetUpClickRecoveryModule(rootFound: true);
        var order = new EngineOrder();

        var cut = RenderForm(order);

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["registerClickRecovery"]));
        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, string.Empty)),
            module.Invocations["registerClickRecovery"].Single().Arguments[0]);
        Assert.DoesNotContain(loggerProvider.Entries, entry => entry.Message.Contains("displaced-click guard"));

        await Services.DisposeAsync();
    }

    /// <summary>
    /// The owned root, staged as bare as the attached one above — disclosure forced rather than
    /// earned by rendered inputs, nothing in the content but a submit button — so that a test
    /// putting the two side by side varies only which component owns the submit.
    /// </summary>
    private IRenderedComponent<FormidableForm<EngineOrder>> RenderOwnedForm(EngineOrder order) =>
        Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            builder.AddComponentParameter(
                2,
                nameof(FormidableForm<EngineOrder>.Options),
                new FormidableOptions { DisclosureOverride = _ => true });
            builder.AddComponentParameter(
                3,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment)(inner => inner.AddMarkupContent(0, "<button type=\"submit\">Go</button>")));
            builder.CloseComponent();
        }).FindComponent<FormidableForm<EngineOrder>>();

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

    // Attach mode's own submit entry point exists so a page driving its own <form> gets the whole
    // FormidableForm submit story minus the element: the blocked submit lands the visitor on the
    // first error, rather than leaving them to hunt for it because the page called the engine
    // directly.
    [Fact]
    public async Task A_blocked_submit_through_the_validator_focuses_the_first_error()
    {
        Services.AddFormidableBlazor();
        var module = SetUpFocusModule();
        var order = new EngineOrder { Description = "ok" }; // only Customer fails
        var cut = RenderForm(order, new FormidableOptions { DisclosureOverride = _ => true });
        var validator = cut.FindComponent<FormidableValidator<EngineOrder>>();

        SubmitOutcome? outcome = null;
        await cut.InvokeAsync(async () => outcome = await validator.Instance.ValidateForSubmitAsync());

        Assert.False(outcome!.CanProceed);
        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, nameof(EngineOrder.Customer))),
            module.Invocations["focusField"].Single().Arguments[0]);

        await Services.DisposeAsync();
    }

    // The same opt-out FormidableForm offers, for a page that would rather choose the landing spot
    // itself from the outcome it is handed back.
    [Fact]
    public async Task FocusFirstErrorOnInvalidSubmit_false_leaves_the_validators_focus_alone()
    {
        Services.AddFormidableBlazor();
        var module = SetUpFocusModule();
        var cut = RenderForm(
            new EngineOrder(),
            new FormidableOptions { DisclosureOverride = _ => true },
            focusFirstErrorOnInvalidSubmit: false);
        var validator = cut.FindComponent<FormidableValidator<EngineOrder>>();

        await cut.InvokeAsync(() => validator.Instance.ValidateForSubmitAsync());

        Assert.DoesNotContain("focusField", module.Invocations.Identifiers);

        await Services.DisposeAsync();
    }

    // Mirrors FormidableFormComponentTests' A_blocked_submit_focus_miss_invokes_the_fallback_and_
    // retries_once: the same miss signal (focusField answering false), the same
    // try -> fallback -> retry shape, reached through attach mode's own parameter.
    [Fact]
    public async Task A_validator_focus_miss_invokes_the_fallback_and_retries_once()
    {
        Services.AddFormidableBlazor();
        var module = SetUpFocusModule(focusLands: false);
        var order = new EngineOrder { Description = "ok" }; // only Customer fails
        var fallbackCalls = 0;
        FieldIdentifier? fallbackField = null;
        var cut = RenderForm(
            order,
            new FormidableOptions { DisclosureOverride = _ => true },
            focusFallback: field =>
            {
                fallbackCalls++;
                fallbackField = field;
                return ValueTask.FromResult(true);
            });
        var validator = cut.FindComponent<FormidableValidator<EngineOrder>>();

        await cut.InvokeAsync(() => validator.Instance.ValidateForSubmitAsync());

        Assert.Equal(1, fallbackCalls);
        Assert.Equal(new FieldIdentifier(order, nameof(EngineOrder.Customer)), fallbackField);
        Assert.Equal(2, module.Invocations["focusField"].Count);

        await Services.DisposeAsync();
    }

    // Which field a blocked submit takes the visitor to is ONE decision, not two that happen to
    // agree today. The staging is the one shape the two candidate rules answer differently: an
    // advisory reported ahead of the error that is actually blocking, so "the first error" lands
    // on Customer.Name and "the first issue" would land on Description. Both roots are asserted
    // against that same shape over the same model, so a change to the rule inside FirstErrorFocus
    // moves both targets together, and a copy of the rule living privately in FormidableValidator
    // would let one root move while the other stayed where it was.
    //
    // The two roots reach the same reported order by the only route each has: the owned form
    // resolves it through IFormidableFieldOrderService, and attach mode — which resolves no order
    // service, and is not given one here either — is handed the equivalent map directly. What is
    // being pinned is what the rule does with the order, not where the order came from.
    [Fact]
    public async Task Both_roots_focus_the_first_error_reported_behind_an_advisory()
    {
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, AdvisoryAboveErrorValidator>();
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var customerName = new FieldIdentifier(order.Customer!, nameof(EngineCustomer.Name));
        Services.AddSingleton<IFormidableFieldOrderService>(
            new RecordingFieldOrderService { Result = [description, customerName] });
        Services.AddFormidableBlazor();
        var module = SetUpFocusModule();

        var attached = RenderForm(order, new FormidableOptions { DisclosureOverride = _ => true });
        var validator = attached.FindComponent<FormidableValidator<EngineOrder>>();
        ((FormValidationEngine<EngineOrder>)validator.Instance.Engine!).SetFieldOrder(
            new Dictionary<FieldIdentifier, int> { [description] = 0, [customerName] = 1 });

        await attached.InvokeAsync(() => validator.Instance.ValidateForSubmitAsync());
        var attachedTarget = Assert.Single(module.Invocations["focusField"]).Arguments[0];

        var owned = RenderOwnedForm(order);
        await owned.InvokeAsync(() => owned.Instance.SubmitAsync());
        Assert.Equal(2, module.Invocations["focusField"].Count);
        var ownedTarget = module.Invocations["focusField"][1].Arguments[0];

        // The staging, asserted rather than assumed: on both roots the advisory really is what
        // reports first, so the two candidate rules genuinely disagree about where to land.
        Assert.Equal(
            "Description could be clearer",
            validator.Instance.Engine!.GetVisibleIssues()[0].Issue.Message);
        Assert.Equal(
            "Description could be clearer",
            owned.Instance.Engine!.GetVisibleIssues()[0].Issue.Message);

        // One assertion over both roots rather than two, so a rule that moved reports BOTH targets
        // moving rather than stopping at whichever root happened to be asserted first.
        var firstError = FormidableFieldId.For(customerName);
        Assert.Equal(new object?[] { firstError, firstError }, new[] { attachedTarget, ownedTarget });

        await Services.DisposeAsync();
    }

    // A submit awaiting the engine's pipeline can still be in flight when the cascaded EditContext
    // is replaced out from under it, which is what OnParametersSet disposes and rebuilds the
    // engine for. The counterpart of FormidableFormComponentTests'
    // SubmitAsync_suppresses_callbacks_when_ResetAsync_disposes_its_engine_mid_flight, for the
    // root whose engine a page reaches through this component rather than owning. Two things make
    // it discriminating at once: the validator does not observe its token, so the abandoned pass
    // runs to completion instead of being cut short by the disposal, and it FAILS, so the verdict
    // the guard has to suppress is one that carries both a summary and a field for focus to move
    // to. Without the guard the outcome reports that stale summary and the focus service is asked
    // for a field on a model nothing is editing any more.
    [Fact]
    public async Task A_submit_whose_engine_is_replaced_mid_flight_reports_blocked_and_moves_nothing()
    {
        Services.AddFormidableBlazor();
        var module = SetUpFocusModule();
        var validator = new CancellationIgnoringValidator { ShouldPass = false };
        var order = new EngineOrder();

        var host = Render<SwappableContextHost>(parameters => parameters
            .Add(p => p.Model, order)
            .Add(p => p.Validator, new FluentValidationModelValidator<EngineOrder>(validator)));
        var attached = host.FindComponent<FormidableValidator<EngineOrder>>();

        SubmitOutcome? outcome = null;
        var submitTask = host.InvokeAsync(
            async () => outcome = await attached.Instance.ValidateForSubmitAsync());
        host.WaitForAssertion(() => Assert.True(validator.Started >= 1));

        // A fresh EditContext over a fresh model reaches OnParametersSet, which disposes the
        // engine the submit above is still awaiting and builds a replacement.
        host.Render(parameters => parameters
            .Add(p => p.Model, new EngineOrder())
            .Add(p => p.Validator, new FluentValidationModelValidator<EngineOrder>(validator)));

        validator.Gate.SetResult(); // resolves - and fails - despite the pass's own token being cancelled
        await submitTask;

        Assert.NotNull(outcome);
        Assert.False(outcome!.CanProceed);
        Assert.Empty(outcome.VisibleErrorSummary); // the abandoned pass's own errors would fill this
        Assert.DoesNotContain("focusField", module.Invocations.Identifiers);

        await Services.DisposeAsync();
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
    // OnRenderedFieldsChanged to run at all (it prunes the live channel and arms the refresh),
    // but a departed row's SUBMIT-time error is untouched by that prune; it clears only once the
    // refresh actually re-validates against the model and finds the row's rule no longer firing
    // (see OnRenderedFieldsChanged's own remarks). The refresh is a real timer independent of the
    // renderer's dispatch queue, so only real time — not another drain — can wait for it.
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

    /// <summary>
    /// Cascades an <see cref="EditContext"/> the host owns, rebuilt whenever <see cref="Model"/>
    /// is swapped, with a <see cref="FormidableValidator{TModel}"/> beneath it. Cascading by hand
    /// rather than through an <c>EditForm</c> is what keeps the same validator instance in place
    /// across the swap: an <c>EditForm</c> renders its subtree inside a region keyed on its
    /// EditContext, so a swap there tears the component down instead of letting it observe the
    /// replacement through <c>OnParametersSet</c> - which is the branch under test.
    /// </summary>
    private sealed class SwappableContextHost : ComponentBase
    {
        // One instance for the life of the host: FormidableValidator reads Options once, when it
        // builds the engine, and rejects a DIFFERENT instance arriving on a render that does not
        // also rebuild the engine.
        private readonly FormidableOptions _options = new() { DisclosureOverride = _ => true };
        private EditContext? _editContext;
        private EngineOrder? _boundModel;

        [Parameter]
        public EngineOrder Model { get; set; } = default!;

        [Parameter]
        public IModelValidator<EngineOrder>? Validator { get; set; }

        protected override void OnParametersSet()
        {
            if (!ReferenceEquals(_boundModel, Model))
            {
                _boundModel = Model;
                _editContext = new EditContext(Model);
            }
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<EditContext>>(0);
            builder.AddComponentParameter(1, "Value", _editContext);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                inner.AddComponentParameter(
                    1, nameof(FormidableValidator<EngineOrder>.Options), _options);
                if (Validator is not null)
                {
                    inner.AddComponentParameter(
                        2, nameof(FormidableValidator<EngineOrder>.Validator), Validator);
                }
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
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

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
