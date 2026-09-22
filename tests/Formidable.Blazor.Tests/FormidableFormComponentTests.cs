using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Formidable.Blazor.Tests;

public class FormidableFormComponentTests : BunitContext
{
    public FormidableFormComponentTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderForm(
        EngineOrder order,
        Action<FormidableInvalidSubmitContext>? onInvalid = null,
        Action? onValid = null,
        bool? focusFirstErrorOnInvalidSubmit = null,
        Action<EngineOrder>? onModelChanged = null,
        Func<FieldIdentifier, ValueTask<bool>>? focusFallback = null,
        FormidableOptions? options = null)
    {
        var container = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            builder.AddComponentParameter(
                2,
                nameof(FormidableForm<EngineOrder>.Options),
                options ?? new FormidableOptions { DisclosureOverride = _ => true });
            builder.AddComponentParameter(
                3,
                nameof(FormidableForm<EngineOrder>.OnInvalidSubmit),
                EventCallback.Factory.Create<FormidableInvalidSubmitContext>(
                    this,
                    context => onInvalid?.Invoke(context)));
            builder.AddComponentParameter(
                4,
                nameof(FormidableForm<EngineOrder>.OnValidSubmit),
                EventCallback.Factory.Create<SubmitOutcome>(this, () => onValid?.Invoke()));
            builder.AddComponentParameter(
                5,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment<FormidableFormContext>)(_ => inner => inner.AddMarkupContent(0, "<button type=\"submit\">Go</button>")));
            if (focusFirstErrorOnInvalidSubmit is { } focusParameter)
            {
                builder.AddComponentParameter(
                    6, nameof(FormidableForm<EngineOrder>.FocusFirstErrorOnInvalidSubmit), focusParameter);
            }

            if (onModelChanged is not null)
            {
                builder.AddComponentParameter(
                    7,
                    nameof(FormidableForm<EngineOrder>.ModelChanged),
                    EventCallback.Factory.Create<EngineOrder>(this, onModelChanged));
            }

            if (focusFallback is not null)
            {
                builder.AddComponentParameter(8, nameof(FormidableForm<EngineOrder>.FocusFallback), focusFallback);
            }

            builder.CloseComponent();
        });

        return container.FindComponent<FormidableForm<EngineOrder>>();
    }

    [Fact]
    public void Renders_a_form_element_with_child_content()
    {
        var cut = RenderForm(new EngineOrder());

        Assert.NotNull(cut.Find("form"));
        Assert.NotNull(cut.Find("button[type=submit]"));
    }

    [Fact]
    public void Invalid_submit_invokes_callback_with_outcome_and_shows_messages()
    {
        SubmitOutcome? outcome = null;
        var cut = RenderForm(new EngineOrder(), onInvalid: context => outcome = context.Outcome);

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(outcome);
            Assert.False(outcome!.CanProceed);
            Assert.Contains("Order description", outcome.VisibleErrorSummary);
        });
    }

    [Fact]
    public async Task Blocked_submit_focuses_the_first_visible_issues_field_by_default()
    {
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(true);
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
        var order = new EngineOrder { Description = "ok" }; // only Customer fails
        var cut = RenderForm(order);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, nameof(EngineOrder.Customer))),
            module.Invocations["focusField"].Single().Arguments[0]);

        await Services.DisposeAsync();
    }

    // Mirrors FormidableSummaryTests' "Fallback_is_invoked_on_focus_miss_and_true_triggers_one_retry":
    // the same miss signal (focusField's false return), the same try -> fallback -> retry shape,
    // now exercised through the form's own auto-focus path instead of a summary click.
    [Fact]
    public async Task A_blocked_submit_focus_miss_invokes_the_fallback_and_retries_once()
    {
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(false);
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
        var order = new EngineOrder { Description = "ok" }; // only Customer fails
        var fallbackCalls = 0;
        FieldIdentifier? fallbackField = null;
        var cut = RenderForm(order, focusFallback: field =>
        {
            fallbackCalls++;
            fallbackField = field;
            return ValueTask.FromResult(true);
        });

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.Equal(1, fallbackCalls);
        Assert.Equal(new FieldIdentifier(order, nameof(EngineOrder.Customer)), fallbackField);
        JSInterop.VerifyInvoke("focusField", 2);

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task A_rendered_first_error_never_invokes_the_fallback()
    {
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(true);
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
        var order = new EngineOrder { Description = "ok" }; // only Customer fails
        var fallbackCalls = 0;
        var cut = RenderForm(order, focusFallback: _ =>
        {
            fallbackCalls++;
            return ValueTask.FromResult(true);
        });

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.Equal(0, fallbackCalls);
        JSInterop.VerifyInvoke("focusField", 1);

        await Services.DisposeAsync();
    }

    // Mirrors SuppressedIssueLoggingTests' logger-capturing shape for the engine's own
    // ReportSuppressed dual channel; this pins the form's parallel diagnostic for a focus miss
    // with no FocusFallback wired, so a consumer's console points at the seam by name.
    [Fact]
    public async Task A_focus_miss_without_a_fallback_reports_a_diagnostic()
    {
        var loggerProvider = new CapturingLoggerProvider();
        Services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddProvider(loggerProvider)));
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(false);
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
        var order = new EngineOrder { Description = "ok" }; // only Customer fails
        var cut = RenderForm(order);

        var exception = await Record.ExceptionAsync(() => cut.InvokeAsync(() => cut.Instance.SubmitAsync()));

        Assert.Null(exception);
        Assert.Contains(
            loggerProvider.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("FocusFallback"));

        await Services.DisposeAsync();
    }

    // The move falls back to the first visible issue of ANY severity when it finds no error, so
    // the field it reports a miss for is not always an error's. A page asking for the move is the
    // short route into that state: nothing blocked, and the only thing on screen is a warning.
    // What this pins is the diagnostic's honesty about severity rather than its wording — any
    // rephrasing that still names the field and stops calling an advisory an error keeps it green.
    [Fact]
    public async Task A_focus_miss_on_an_advisory_reports_it_without_calling_it_an_error()
    {
        var loggerProvider = new CapturingLoggerProvider();
        Services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(builder => builder.AddProvider(loggerProvider)));
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(false);
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();

        // Every error rule passes and the hyphen rule does not, so Description's warning is the
        // whole of what is on screen and the submit itself goes through.
        var order = new EngineOrder
        {
            Description = "ok-",
            Customer = new EngineCustomer { Name = "Ada" },
        };
        var cut = RenderForm(order);
        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.False(await cut.InvokeAsync(() => cut.Instance.FocusFirstErrorAsync()));

        // Single, not Contains, and it carries two claims. That the diagnostic fired at all is
        // what proves the move reached the advisory rather than finding nothing to aim at and
        // returning early; that it fired ONCE is what proves the submit made no move of its own,
        // since a blocked one under the default parameter would have missed and reported too.
        var entry = Assert.Single(
            loggerProvider.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("FocusFallback"));
        Assert.Contains(nameof(EngineOrder.Description), entry.Message);
        Assert.DoesNotContain("error", entry.Message, StringComparison.OrdinalIgnoreCase);

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task FocusFirstErrorOnInvalidSubmit_false_skips_the_focus_call()
    {
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(true);
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
        var cut = RenderForm(new EngineOrder(), focusFirstErrorOnInvalidSubmit: false);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.DoesNotContain("focusField", module.Invocations.Identifiers);

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task Valid_submit_does_not_focus_anything()
    {
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(true);
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var cut = RenderForm(order);

        await cut.InvokeAsync(() => cut.Instance.SubmitAsync());

        Assert.DoesNotContain("focusField", module.Invocations.Identifiers);

        await Services.DisposeAsync();
    }

    [Fact]
    public void Valid_submit_invokes_valid_callback()
    {
        var valid = false;
        var order = new EngineOrder { Description = "ok", Customer = new EngineCustomer() };
        var cut = RenderForm(order, onValid: () => valid = true);

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(valid));
    }

    [Fact]
    public async Task Valid_submit_passes_the_outcome_to_OnValidSubmit()
    {
        SubmitOutcome? received = null;
        var model = new EngineOrder { Description = "Valid desc", Customer = new EngineCustomer() };
        var cut = Render<FormidableForm<EngineOrder>>(ps => ps
            .Add(p => p.Model, model)
            .Add(p => p.OnValidSubmit, (SubmitOutcome o) => { received = o; }));

        SubmitOutcome? outcome = null;
        await cut.InvokeAsync(async () => outcome = await cut.Instance.SubmitAsync());

        Assert.NotNull(received);
        Assert.True(received!.CanProceed);
        Assert.Same(outcome, received);
    }

    [Fact]
    public void Model_swap_rebuilds_edit_context_and_resets_state()
    {
        var first = new EngineOrder();
        var cut = RenderForm(first);
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.True(cut.Instance.Engine!.HasSubmitted));

        var second = new EngineOrder();
        cut.Render(parameters => parameters.Add(p => p.Model, second));

        Assert.False(cut.Instance.Engine!.HasSubmitted);
        Assert.Same(second, cut.Instance.Engine.EditContext.Model);
    }

    // Safety verification for the form's IsFixed="true" cascade: a Model swap must still reach a
    // BARE kit input (no FormidableField wrapper) with the new engine, even though the cascade no
    // longer notifies subscribers on every unrelated re-render. It does, because the cascade's own
    // region is keyed on the form's context — the same context a Model swap replaces — so the swap
    // destroys the cascade itself and everything below it, and the fresh instances that replace
    // them (the input included) are mounted for the first time, reading the new context on their
    // own first render rather than depending on a notification they never needed.
    [Fact]
    public void Model_swap_still_rebinds_a_bare_input_to_the_new_engine()
    {
        var first = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", first);
            builder.AddComponentParameter(2, "ChildContent", InputBoundTo(first, this));
            builder.CloseComponent();
        });

        var form = cut.FindComponent<FormidableForm<EngineOrder>>();
        var firstEngine = form.Instance.Engine!;
        Assert.True(firstEngine.Registry.IsRegistered(new FieldIdentifier(first, nameof(EngineOrder.Description))));

        var second = new EngineOrder();
        form.Render(parameters => parameters
            .Add(p => p.Model, second)
            .Add(p => p.ChildContent, InputBoundTo(second, this)));

        var secondEngine = form.Instance.Engine!;
        Assert.NotSame(firstEngine, secondEngine);
        var secondField = new FieldIdentifier(second, nameof(EngineOrder.Description));
        Assert.True(secondEngine.Registry.IsRegistered(secondField));

        // The rebuilt input's live pass must reach the NEW engine — if the input were still
        // wired to the disposed first engine, this would never turn invalid and the
        // WaitForAssertion below would time out.
        second.Description = new string('x', 11);
        cut.InvokeAsync(() => secondEngine.EditContext.NotifyFieldChanged(secondField));

        cut.WaitForAssertion(() => Assert.Contains("formidable-invalid", cut.Find("input").GetAttribute("class")));
    }

    private static RenderFragment<FormidableFormContext> InputBoundTo(EngineOrder order, object receiver) => _ => inner =>
    {
        inner.OpenComponent<FormidableInputText>(0);
        inner.AddComponentParameter(1, "For", (System.Linq.Expressions.Expression<Func<string?>>)(() => order.Description));
        inner.AddComponentParameter(2, "Value", order.Description);
        inner.AddComponentParameter(3, "ValueChanged",
            EventCallback.Factory.Create<string?>(receiver, v => order.Description = v ?? string.Empty));
        inner.CloseComponent();
    };

    [Fact]
    public async Task ResetAsync_returns_the_same_instance_to_pristine()
    {
        var order = new EngineOrder();
        var cut = RenderForm(order);
        var descriptionField = new FieldIdentifier(order, nameof(EngineOrder.Description));

        cut.Instance.Engine!.MarkTouched(descriptionField);
        Assert.True(cut.Instance.Engine!.GetFieldState(descriptionField).IsTouched);

        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.True(cut.Instance.Engine!.HasSubmitted));
        Assert.NotEmpty(cut.Instance.Engine!.GetVisibleIssues());

        await cut.InvokeAsync(() => cut.Instance.ResetAsync());

        Assert.Empty(cut.Instance.Engine!.GetVisibleIssues());
        Assert.False(cut.Instance.Engine!.HasSubmitted);
        Assert.False(cut.Instance.Engine!.GetFieldState(descriptionField).IsTouched);
        Assert.Same(order, cut.Instance.Engine.EditContext.Model);
    }

    [Fact]
    public async Task ResetAsync_with_a_new_model_swaps_the_instance()
    {
        var first = new EngineOrder();
        var cut = RenderForm(first, onModelChanged: _ => { });

        var second = new EngineOrder();
        await cut.InvokeAsync(() => cut.Instance.ResetAsync(second));

        Assert.Same(second, cut.Instance.Engine!.EditContext.Model);
    }

    // The swap above alone is not proof the swap is durable: Model is a [Parameter], and Blazor
    // re-supplies it from whatever the PARENT still holds on every one of the PARENT's own
    // renders — not just this component's. A ResetAsync that only assigned its own Model property
    // (never telling the parent) would look identical to the test above right up until the next
    // unrelated render came along and silently reverted it. This test is that next render.
    [Fact]
    public async Task ResetAsync_with_a_new_model_survives_the_parents_next_render()
    {
        var current = new EngineOrder();
        var cut = RenderForm(current, onModelChanged: m => current = m);

        var second = new EngineOrder();
        await cut.InvokeAsync(() => cut.Instance.ResetAsync(second));
        Assert.Same(second, cut.Instance.Engine!.EditContext.Model);

        // Stands in for the parent's own re-render, re-supplying whatever ITS field holds now.
        // `current` was updated above only because ResetAsync invoked ModelChanged before
        // returning — that is the entire mechanism this test pins.
        cut.Render(parameters => parameters.Add(p => p.Model, current));

        Assert.Same(second, cut.Instance.Engine!.EditContext.Model);
    }

    [Fact]
    public async Task ResetAsync_with_a_new_model_and_no_ModelChanged_throws()
    {
        var cut = RenderForm(new EngineOrder());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cut.InvokeAsync(() => cut.Instance.ResetAsync(new EngineOrder())));

        Assert.Contains("@bind-Model", ex.Message);
    }

    // Safety verification for VerifyRowKeys across ResetAsync: the rebuild must replace _context
    // with a NEW instance (not merely rebuild the engine underneath the same one), because
    // FormidableComponentBase's row-key check only re-registers a field when its cascade binding
    // sees a new context instance. Reusing the old one would leave every already-mounted field
    // component bound to it, comparing against a registration nothing rebuilt.
    [Fact]
    public async Task ResetAsync_does_not_trip_VerifyRowKeys_on_a_rendered_field()
    {
        var order = new EngineOrder();
        var options = new FormidableOptions { VerifyRowKeys = true };
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "Options", options);
            builder.AddComponentParameter(3, "ChildContent", InputBoundTo(order, this));
            builder.CloseComponent();
        });

        var form = cut.FindComponent<FormidableForm<EngineOrder>>();
        var descriptionField = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await form.InvokeAsync(() => form.Instance.ResetAsync());

        // Proves the rebuild is a genuine rebind, not merely silent: the field is freshly
        // registered against the NEW engine's own registry (a reused context would leave the
        // input still bound to the OLD, disposed engine, so Register() never runs again and
        // this reads false).
        Assert.True(form.Instance.Engine!.Registry.IsRegistered(descriptionField));

        // A second render over the now-stable (post-reset) context, Model/Options unchanged,
        // takes the rebuilt input's OnParametersSet down the VerifyRowKey no-op path rather
        // than its first-bind path.
        form.Render(parameters => parameters
            .Add(p => p.Model, order)
            .Add(p => p.Options, options)
            .Add(p => p.ChildContent, InputBoundTo(order, this)));

        Assert.NotNull(cut.Find("input"));
        Assert.True(form.Instance.Engine!.Registry.IsRegistered(descriptionField));
    }

    // A submit awaiting the engine's pipeline can still be in flight when ResetAsync disposes
    // that very engine out from under it. Cancelling the abandoned pass's token is what usually
    // ends it early (see FormValidationEngine's own "superseded, not cancelled by the caller"
    // handling), but this validator does not observe its token at all — the pass runs to
    // completion and would pass, proving that what blocks this outcome is FormidableForm's own
    // dead-engine guard, not supersession inside the engine. Its verdict belongs to an abandoned
    // engine and must not surface as if it were current — CanProceed included.
    [Fact]
    public async Task SubmitAsync_suppresses_callbacks_when_ResetAsync_disposes_its_engine_mid_flight()
    {
        var order = new EngineOrder();
        var validator = new CancellationIgnoringValidator();
        var validSeen = false;
        var invalidSeen = false;

        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "Validator", new FluentValidationModelValidator<EngineOrder>(validator));
            builder.AddComponentParameter(
                3,
                "OnValidSubmit",
                EventCallback.Factory.Create<SubmitOutcome>(this, () => validSeen = true));
            builder.AddComponentParameter(
                4,
                "OnInvalidSubmit",
                EventCallback.Factory.Create<FormidableInvalidSubmitContext>(
                    this,
                    _ => invalidSeen = true));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();

        SubmitOutcome? outcome = null;
        var submitTask = form.InvokeAsync(async () => outcome = await form.Instance.SubmitAsync());
        form.WaitForAssertion(() => Assert.True(validator.Started >= 1));

        await form.InvokeAsync(() => form.Instance.ResetAsync());
        validator.Gate.SetResult(); // resolves — and passes — despite the pass's own token being cancelled
        await submitTask;

        Assert.NotNull(outcome);
        Assert.False(outcome!.CanProceed); // would be true here if the raw (passing) outcome leaked through
        Assert.Empty(outcome.VisibleErrorSummary);
        Assert.False(validSeen);
        Assert.False(invalidSeen);
    }

    [Fact]
    public async Task SubmitAsync_is_available_programmatically()
    {
        var cut = RenderForm(new EngineOrder());

        SubmitOutcome? outcome = null;
        await cut.InvokeAsync(async () => outcome = await cut.Instance.SubmitAsync());

        Assert.NotNull(outcome);
        Assert.False(outcome!.CanProceed);
    }

    // The root forwards the engine's cancellation contract whole: a cancelled call throws and
    // adopts nothing. Swallowing the token in the pass-through would let the load run to
    // completion — no throw, Description touched and disclosing — so every assertion here
    // discriminates that regression.
    [Fact]
    public async Task A_pre_cancelled_disclose_token_throws_and_adopts_nothing()
    {
        var order = new EngineOrder { Description = "far too long for the ten-character rule" };
        var cut = RenderForm(order);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cut.InvokeAsync(() => cut.Instance.DiscloseLoadedValuesAsync(cts.Token)));

        var engine = cut.Instance.Engine!;
        Assert.False(engine.GetFieldState(description).IsTouched);
        Assert.Empty(engine.EditContext.GetValidationMessages());

        // The engagement half of "adopts nothing": a live pass answers every engaged field, so a
        // field the cancelled call had wrongly engaged would disclose its failing value here.
        // Customer's own error appearing is the positive control that the pass ran and disclosed.
        await cut.InvokeAsync(() => engine.EditContext.NotifyFieldChanged(
            new FieldIdentifier(order, nameof(EngineOrder.Customer))));
        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(engine.EditContext.GetValidationMessages(
                new FieldIdentifier(order, nameof(EngineOrder.Customer))));
            Assert.Empty(engine.EditContext.GetValidationMessages(description));
        });
    }

    // The server round trip is the one pipeline step a page drives itself, and reaching it through
    // the engine property costs two null-forgiving operators on a reference the page already holds.
    [Fact]
    public async Task ApplyServerIssues_is_forwarded_to_the_engine()
    {
        var order = new EngineOrder();
        var cut = RenderForm(order);

        await cut.InvokeAsync(() => cut.Instance.ApplyServerIssues(
            [new ValidationIssue(nameof(EngineOrder.Description), "Server rejected this description")]));

        Assert.Contains(
            "Server rejected this description",
            cut.Instance.Engine!.EditContext.GetValidationMessages(
                new FieldIdentifier(order, nameof(EngineOrder.Description))));
    }

    [Fact]
    public async Task ApplyServerIssues_takes_a_deserialized_problem_body_directly()
    {
        var order = new EngineOrder();
        var cut = RenderForm(order);
        var problem = new FormidableValidationProblem
        {
            Errors = { [nameof(EngineOrder.Description)] = ["Server rejected this description"] },
        };

        await cut.InvokeAsync(() => cut.Instance.ApplyServerIssues(problem));

        Assert.Contains(
            "Server rejected this description",
            cut.Instance.Engine!.EditContext.GetValidationMessages(
                new FieldIdentifier(order, nameof(EngineOrder.Description))));
    }

    // A rejected round trip is a blocked submit that arrived late, so applying its verdict
    // focuses the same way — see the two SubmitAsync focus tests above for the mirrored pair.
    [Fact]
    public async Task Applying_server_issues_focuses_the_first_problem()
    {
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(true);
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
        var order = new EngineOrder();
        var cut = RenderForm(order);

        await cut.InvokeAsync(() => cut.Instance.ApplyServerIssues(
            [new ValidationIssue(nameof(EngineOrder.Description), "server says no")]));

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, nameof(EngineOrder.Description))),
            module.Invocations["focusField"].Single().Arguments[0]);

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task Applying_server_issues_does_not_focus_when_the_parameter_is_off()
    {
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(true);
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
        var order = new EngineOrder();
        var cut = RenderForm(order, focusFirstErrorOnInvalidSubmit: false);

        await cut.InvokeAsync(() => cut.Instance.ApplyServerIssues(
            [new ValidationIssue(nameof(EngineOrder.Description), "server says no")]));

        Assert.DoesNotContain("focusField", module.Invocations.Identifiers);

        await Services.DisposeAsync();
    }

    // An accepted resubmission carries nothing to reject, so it must not steal focus onto some
    // unrelated issue an EARLIER, unrelated submit left visible on the page — only what THIS
    // apply itself rejected is grounds to move focus. FocusFirstErrorOnInvalidSubmit is switched
    // on only after the (deliberately unfocused) first submit, so the stale error's own
    // visibility is not itself a confound.
    [Fact]
    public async Task Applying_an_empty_verdict_does_not_focus_a_stale_visible_issue()
    {
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(true);
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
        var order = new EngineOrder();
        var cut = RenderForm(order, focusFirstErrorOnInvalidSubmit: false);

        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.True(cut.Instance.Engine!.HasSubmitted));
        Assert.NotEmpty(cut.Instance.Engine!.GetVisibleIssues());
        Assert.DoesNotContain("focusField", module.Invocations.Identifiers);

        cut.Render(parameters => parameters.Add(p => p.FocusFirstErrorOnInvalidSubmit, true));

        await cut.InvokeAsync(() => cut.Instance.ApplyServerIssues(Array.Empty<ValidationIssue>()));

        Assert.DoesNotContain("focusField", module.Invocations.Identifiers);

        await Services.DisposeAsync();
    }

    // An advisory blocks nothing, so an apply carrying only advisories rejected nothing either —
    // the same "nothing to reject" case as an empty apply, just with a warning attached.
    [Fact]
    public async Task Applying_advisory_only_server_issues_does_not_focus_anything()
    {
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.Setup<bool>("focusField", _ => true).SetResult(true);
        module.Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
        module.SetupVoid("observeLayout", _ => true).SetVoidResult();
        module.SetupVoid("disconnectLayoutObserver", _ => true).SetVoidResult();
        var order = new EngineOrder();
        var cut = RenderForm(order);

        await cut.InvokeAsync(() => cut.Instance.ApplyServerIssues(
            [new ValidationIssue(nameof(EngineOrder.Description), "a gentle nudge", ValidationSeverity.Warning)]));

        Assert.DoesNotContain("focusField", module.Invocations.Identifiers);

        await Services.DisposeAsync();
    }

    // Both forwards and SubmitAsync run through the engine the form builds on its first parameter
    // set, so a call that beats that render has nothing to run on. It says so by name rather than
    // surfacing as a bare NullReferenceException from inside the component.
    [Fact]
    public async Task Calls_before_the_engine_exists_name_the_form_and_the_reference()
    {
        var form = new FormidableForm<EngineOrder>();

        var byIssues = Assert.Throws<InvalidOperationException>(() => form.ApplyServerIssues([]));
        var byProblem = Assert.Throws<InvalidOperationException>(
            () => form.ApplyServerIssues(new FormidableValidationProblem()));
        var bySubmit = await Assert.ThrowsAsync<InvalidOperationException>(() => form.SubmitAsync());

        Assert.Contains("FormidableForm", byIssues.Message);
        Assert.Contains("@ref", byIssues.Message);
        Assert.Equal(byIssues.Message, byProblem.Message);
        Assert.Equal(byIssues.Message, bySubmit.Message);
    }

    [Fact]
    public void Form_element_carries_the_model_level_id_and_tabindex()
    {
        var order = new EngineOrder();
        var cut = RenderForm(order);

        var form = cut.Find("form");
        Assert.Equal(FormidableFieldId.For(new FieldIdentifier(order, string.Empty)), form.GetAttribute("id"));
        Assert.Equal("-1", form.GetAttribute("tabindex"));
    }

    [Fact]
    public void Consumer_supplied_id_and_tabindex_are_ignored()
    {
        // Mirrors FormidableInputBase<TValue>'s own policy: the deterministic id is what the
        // focus service and the all-suppressed gate address the form by, so a consumer-splatted
        // id/tabindex loses the duplicate-attribute race rather than winning it.
        var order = new EngineOrder();
        var container = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            builder.AddAttribute(2, "id", "consumer-id");
            builder.AddAttribute(3, "tabindex", "3");
            builder.AddComponentParameter(
                4,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment<FormidableFormContext>)(_ => inner => inner.AddMarkupContent(0, "<button type=\"submit\">Go</button>")));
            builder.CloseComponent();
        });

        var form = container.Find("form");
        Assert.Equal(FormidableFieldId.For(new FieldIdentifier(order, string.Empty)), form.GetAttribute("id"));
        Assert.Equal("-1", form.GetAttribute("tabindex"));
    }

    // The typed fragment must receive the SAME context instance the form cascades — not a copy,
    // not a fresh wrapper — so inline reads and the cascade agree about which engine they speak
    // for. Handing the fragment anything else would let context.Engine and a nested component's
    // cascaded Engine diverge after a model swap.
    [Fact]
    public void ChildContent_receives_the_cascaded_context_instance()
    {
        FormidableFormContext? received = null;
        var order = new EngineOrder();
        var container = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            builder.AddComponentParameter(
                2,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment<FormidableFormContext>)(context => _ => received = context));
            builder.CloseComponent();
        });

        var cascade = container.FindComponent<CascadingValue<FormidableFormContext>>();
        Assert.NotNull(received);
        Assert.Same(cascade.Instance.Value, received);
        Assert.Same(container.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine, received!.Engine);
    }

    [Fact]
    public void Form_element_renders_novalidate_by_default()
    {
        // The deliberate default: native constraint validation would front the browser's own
        // bubble and refuse the submit before OnSubmit ever fired, so no message would stay
        // FluentValidation's. novalidate switches off exactly that interactive check.
        var cut = RenderForm(new EngineOrder());

        Assert.True(cut.Find("form").HasAttribute("novalidate"));
    }

    [Fact]
    public void A_splatted_novalidate_false_removes_the_default()
    {
        // Unlike id/tabindex, novalidate renders BEFORE the splat — the consumer-wins position —
        // so a page that wants the browser's native constraint UI back can splat it away with
        // novalidate="@false" (a bool false removes the attribute; the string "false" would
        // still render it, and a rendered novalidate is on whatever its value says).
        var order = new EngineOrder();
        var container = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), order);
            builder.AddAttribute(2, "novalidate", false);
            builder.AddComponentParameter(
                3,
                nameof(FormidableForm<EngineOrder>.ChildContent),
                (RenderFragment<FormidableFormContext>)(_ => inner => inner.AddMarkupContent(0, "<button type=\"submit\">Go</button>")));
            builder.CloseComponent();
        });

        Assert.False(container.Find("form").HasAttribute("novalidate"));
    }

    [Fact]
    public void Removing_a_row_after_a_submit_clears_its_summary_entry()
    {
        // The summary is the only component here that injects one, and the moves it would make are
        // beside the point.
        Services.AddSingleton<IFormidableFocusService, SilentFocusService>();

        var keep = new EngineItem { Sku = "keep" };
        var doomed = new EngineItem();
        var order = new EngineOrder
        {
            Customer = new EngineCustomer(),
            Items = [keep, doomed],
        };

        var host = Render<CollectionHost>(parameters => parameters
            .Add(p => p.Order, order)
            // Shorter than the default so the reconciliation this pins does not hold the test open
            // for most of a second waiting for it.
            .Add(p => p.Options, new FormidableOptions { RefreshDebounce = TimeSpan.FromMilliseconds(20) }));

        host.Find("form").Submit();

        // Two disclosed errors, so what follows can tell a summary that dropped one entry from a
        // summary that stopped rendering. Awaited rather than read straight off the markup: the
        // submit's pass is asynchronous, so the disclosure this scenario is built on lands after
        // Submit returns rather than within it.
        host.WaitForAssertion(() =>
        {
            Assert.Contains(SkuRequired, host.Markup, StringComparison.Ordinal);
            Assert.Contains(DescriptionRequired, host.Markup, StringComparison.Ordinal);
        });

        // Exactly what a page's own Remove button does, and nothing more: the model is mutated and
        // the page re-renders. Nothing announces the change — no NotifyChanged anywhere — which is
        // the whole point. The engine has to notice the row left by itself.
        order.Items.Remove(doomed);
        host.Render(parameters => parameters.Add(p => p.Order, order));

        host.WaitForAssertion(() =>
        {
            Assert.DoesNotContain(SkuRequired, host.Markup, StringComparison.Ordinal);
            Assert.Contains(DescriptionRequired, host.Markup, StringComparison.Ordinal);
        });
    }

    private const string SkuRequired = "SKU is required";

    // EngineOrderValidator names the description before requiring it, so the rendered message is
    // the renamed one rather than the property's own name.
    private const string DescriptionRequired = "Order description";

    /// <summary>
    /// A form over a collection, rendered the way a page renders one: a summary, an anchor for the
    /// form's own scalar field, and one keyed anchor per row, all read from <see cref="Order"/> on
    /// every render so removing a row from the list is all it takes to unregister that row.
    /// </summary>
    private sealed class CollectionHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public FormidableOptions? Options { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), Options);
            builder.AddComponentParameter(
                3, nameof(FormidableForm<EngineOrder>.FocusFirstErrorOnInvalidSubmit), false);
            builder.AddComponentParameter(4, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                inner.CloseComponent();

                inner.OpenComponent<FormidableFieldAnchor<string>>(1);
                inner.AddComponentParameter(
                    2,
                    nameof(FormidableFieldAnchor<string>.For),
                    (Expression<Func<string>>)(() => Order.Description));
                inner.CloseComponent();

                foreach (var item in Order.Items)
                {
                    inner.OpenComponent<FormidableFieldAnchor<string>>(3);
                    inner.SetKey(item);
                    inner.AddComponentParameter(
                        4,
                        nameof(FormidableFieldAnchor<string>.For),
                        (Expression<Func<string>>)(() => item.Sku));
                    inner.CloseComponent();
                }

                inner.AddMarkupContent(5, "<button type=\"submit\">Go</button>");
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Vouches for every focus request, so the summary can render without a move.</summary>
    private sealed class SilentFocusService : IFormidableFocusService
    {
        public ValueTask<bool> FocusAsync(FieldIdentifier field) => ValueTask.FromResult(true);
    }

    // The displaced-click guard's wiring, which is all a component test can reach: bUnit renders
    // no layout and dispatches no pointer events, so a press and a release landing on different
    // targets — the whole shape the guard exists for — cannot be produced here at all. What these
    // pin is that the root asks for the guard, releases it, and honours the opt-out; the behaviour
    // itself is pinned in the browser, in QuickstartJourney.
    [Fact]
    public async Task The_root_asks_the_script_to_guard_its_buttons()
    {
        var module = SetUpClickRecoveryModule();
        var order = new EngineOrder();
        var cut = RenderForm(order);

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["registerClickRecovery"]));
        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, string.Empty)),
            module.Invocations["registerClickRecovery"].Single().Arguments[0]);

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_the_root_releases_the_guard()
    {
        var module = SetUpClickRecoveryModule();
        var order = new EngineOrder();
        var cut = RenderForm(order);
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["registerClickRecovery"]));

        await DisposeComponentsAsync();

        Assert.Equal(
            FormidableFieldId.For(new FieldIdentifier(order, string.Empty)),
            module.Invocations["releaseClickRecovery"].Single().Arguments[0]);

        await Services.DisposeAsync();
    }

    [Fact]
    public async Task ClickRecovery_None_asks_the_script_for_no_guard_at_all()
    {
        var module = SetUpClickRecoveryModule();

        RenderForm(
            new EngineOrder(),
            options: new FormidableOptions
            {
                DisclosureOverride = _ => true,
                ClickRecovery = DisplacedClickRecovery.None,
            });

        Assert.DoesNotContain("registerClickRecovery", module.Invocations.Identifiers);

        await Services.DisposeAsync();
    }

    private BunitJSModuleInterop SetUpClickRecoveryModule()
    {
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.SetupVoid("registerClickRecovery", _ => true).SetVoidResult();
        module.SetupVoid("releaseClickRecovery", _ => true).SetVoidResult();
        return module;
    }

    /// <summary>Mirrors SuppressedIssueLoggingTests' own capturing provider for this file's diagnostic test.</summary>
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
