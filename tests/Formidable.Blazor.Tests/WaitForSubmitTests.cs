using System.Globalization;
using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins <c>WaitForSubmit</c> through the kit: a field a waiting component registers still engages
/// on a commit and its rules still run, and its live messages stay off every surface (inline list,
/// summary, state class, aria-invalid, the edit context's messages) until a submit or server reply
/// has answered. A change of the parameter on a mounted component reaches every surface at that
/// component's next render.
/// </summary>
public class WaitForSubmitTests : BunitContext
{
    private static readonly string TooLong = new('x', 11);

    private void UseServices<TValidator>()
        where TValidator : class, FluentValidation.IValidator<EngineOrder>
    {
        Services.AddFormidableBlazor();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, TValidator>();
        Services.AddSingleton<FluentValidation.IValidator<RowListModel>, RowListValidator>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js")
            .Setup<IReadOnlyList<string>>("orderFields", _ => true).SetResult([]);
    }

    // Attach mode resolves the three JS-backed services too; these doubles go in ahead of
    // AddFormidableBlazor, whose three service registrations are TryAdds.
    private void UseAttachServices()
    {
        Services.AddSingleton<IFormidableFocusService>(new RecordingFocusService());
        Services.AddSingleton<IFormidableFieldOrderService>(new RecordingFieldOrderService());
        Services.AddSingleton<IFormidableDomValueSync>(new RecordingDomValueSync());
        Services.AddFormidableBlazor();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderForm(
        EngineOrder order,
        bool waitForSubmit,
        InputUpdateMode updateOn = InputUpdateMode.OnChange,
        FormidableOptions? options = null)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            if (options is not null)
            {
                builder.AddComponentParameter(2, "Options", options);
            }

            builder.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
                AddSurfaces(inner, this, order, waitForSubmit, updateOn)));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<EngineOrder>>();
    }

    // The input, its message list and a summary, which together carry every live surface.
    private static void AddSurfaces(
        RenderTreeBuilder inner,
        object receiver,
        EngineOrder order,
        bool waitForSubmit,
        InputUpdateMode updateOn = InputUpdateMode.OnChange)
    {
        AddHeldInput(inner, 0, receiver, order, waitForSubmit, updateOn);

        inner.OpenComponent<FormidableFieldMessage<string>>(10);
        inner.AddComponentParameter(11, "For", (Expression<Func<string>>)(() => order.Description));
        inner.CloseComponent();

        inner.OpenComponent<FormidableSummary>(12);
        inner.CloseComponent();
    }

    // The one way every test here builds its input: the description, bound as @bind-Value binds
    // it, waiting or not. It takes eight sequence numbers from sequence.
    private static void AddHeldInput(
        RenderTreeBuilder builder,
        int sequence,
        object receiver,
        EngineOrder order,
        bool waitForSubmit,
        InputUpdateMode updateOn = InputUpdateMode.OnChange,
        string? dataName = null,
        bool keepRegistered = false)
    {
        builder.OpenComponent<FormidableInputText>(sequence);
        builder.AddComponentParameter(sequence + 1, "For", (Expression<Func<string?>>)(() => order.Description));
        builder.AddComponentParameter(sequence + 2, "Value", order.Description);
        builder.AddComponentParameter(sequence + 3, "ValueChanged",
            EventCallback.Factory.Create<string?>(receiver, v => order.Description = v ?? string.Empty));
        builder.AddComponentParameter(sequence + 4, "UpdateOn", updateOn);
        builder.AddComponentParameter(sequence + 5, "WaitForSubmit", waitForSubmit);
        if (dataName is not null)
        {
            builder.AddComponentParameter(sequence + 6, "data-name", dataName);
        }

        if (keepRegistered)
        {
            builder.AddComponentParameter(sequence + 7, "KeepRegistered", true);
        }

        builder.CloseComponent();
    }

    // The native message a ValidationMessage renders for the description.
    private static void AddNativeMessage(RenderTreeBuilder builder, int sequence, EngineOrder order)
    {
        builder.OpenComponent<ValidationMessage<string>>(sequence);
        builder.AddComponentParameter(sequence + 1, "For", (Expression<Func<string>>)(() => order.Description));
        builder.CloseComponent();
    }

    // The dispatcher runs one piece of work at a time. This no-op completes only once the work
    // queued ahead of it has, such as a round posted past a render batch. By the time a change
    // fired inside InvokeAsync completes, its handler's synchronous part (commit, notification and
    // the synchronous live pass) has run.
    private static Task Settle<TComponent>(IRenderedComponent<TComponent> cut)
        where TComponent : IComponent =>
        cut.InvokeAsync(() => { });

    // Every surface a waiting input's field shows on is empty: the inline list, the summary, the
    // input's state class and aria-invalid, and the edit context's messages.
    private static void AssertQuiet<TComponent>(IRenderedComponent<TComponent> cut, EditContext editContext, FieldIdentifier field)
        where TComponent : IComponent
    {
        Assert.Empty(cut.FindAll("ul.formidable-message-list li"));
        Assert.Empty(cut.FindAll("ul.formidable-summary__group--error li"));
        Assert.Empty(cut.FindAll("div.validation-message"));
        Assert.Empty(editContext.GetValidationMessages(field));
        foreach (var input in cut.FindAll("input"))
        {
            Assert.DoesNotContain("formidable-invalid", input.GetAttribute("class") ?? string.Empty);
            Assert.Null(input.GetAttribute("aria-invalid"));
        }
    }

    // The length error shows on every surface: the inline list, the summary, the input's state
    // class and aria-invalid, the edit context's messages, and a native message where one renders.
    private static void AssertShowing<TComponent>(
        IRenderedComponent<TComponent> cut, EditContext editContext, FieldIdentifier field, bool nativeMessage)
        where TComponent : IComponent
    {
        Assert.Contains(cut.FindAll("ul.formidable-message-list li"), li => li.TextContent.Contains("10"));
        Assert.Contains(cut.FindAll("ul.formidable-summary__group--error li"), li => li.TextContent.Contains("10"));
        Assert.Contains(editContext.GetValidationMessages(field), m => m.Contains("10"));
        if (nativeMessage)
        {
            Assert.Contains(cut.FindAll("div.validation-message"), div => div.TextContent.Contains("10"));
        }

        var input = cut.Find("input");
        Assert.Contains("formidable-invalid", input.GetAttribute("class"));
        Assert.Equal("true", input.GetAttribute("aria-invalid"));
    }

    // No surface shows the description's length error: the inline lists, the kit summary, a native
    // summary and message, every input's state class and aria-invalid, and the edit context.
    private static void AssertNothingShows<TComponent>(IRenderedComponent<TComponent> cut, EditContext editContext, FieldIdentifier field)
        where TComponent : IComponent
    {
        AssertQuiet(cut, editContext, field);
        Assert.Equal(0, SurfacesShowing(cut, "10"));
    }

    // How many elements of the named kinds carry the text. With no selector given, the kinds are
    // every surface a message can render on: the kit's message list and summary, and the native
    // summary and message.
    private static int SurfacesShowing<TComponent>(IRenderedComponent<TComponent> cut, string text, params string[] selectors)
        where TComponent : IComponent
    {
        if (selectors.Length == 0)
        {
            selectors =
            [
                "ul.formidable-message-list li",
                "ul.formidable-summary__group--error li",
                "ul.validation-errors li",
                "div.validation-message",
            ];
        }

        return selectors.Sum(selector => cut.FindAll(selector).Count(element => element.TextContent.Contains(text)));
    }

    // Every live surface at once: the inline list, the summary, the state class and aria-invalid.
    // The false row is the control that the same staging discloses on every surface. Mutation:
    // have the input register with false for the hold, and the true row fails.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitForSubmit_engages_on_a_commit_and_holds_every_live_surface(bool waitForSubmit)
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var form = RenderForm(order, waitForSubmit);
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await form.InvokeAsync(() => form.Find("input").Change(TooLong));
        await Settle(form);

        Assert.Equal(TooLong, order.Description);
        var state = form.Instance.Engine!.GetFieldState(description);
        Assert.True(state.IsModified);
        Assert.True(state.IsTouched);

        var input = form.Find("input");
        if (waitForSubmit)
        {
            Assert.DoesNotContain("formidable-invalid", input.GetAttribute("class") ?? string.Empty);
            Assert.Null(input.GetAttribute("aria-invalid"));
            Assert.Empty(form.FindAll("ul.formidable-message-list li"));
            Assert.Empty(form.FindAll("ul.formidable-summary__group--error li"));
        }
        else
        {
            Assert.Contains("formidable-invalid", input.GetAttribute("class"));
            Assert.Equal("true", input.GetAttribute("aria-invalid"));
            Assert.NotEmpty(form.FindAll("ul.formidable-message-list li"));
            Assert.NotEmpty(form.FindAll("ul.formidable-summary__group--error li"));
        }

        await Services.DisposeAsync();
    }

    // Mutation: delete the hold condition in LiveViewOf, or have the input register with false for
    // the hold, and the assertion ahead of the submit fails.
    [Fact]
    public async Task The_first_submit_discloses_a_held_field_on_every_surface()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var form = RenderForm(order, waitForSubmit: true);

        await form.InvokeAsync(() => form.Find("input").Change(TooLong));
        await Settle(form);
        Assert.Empty(form.FindAll("ul.formidable-message-list li"));

        await form.InvokeAsync(() => form.Find("form").Submit());

        form.WaitForAssertion(() =>
        {
            var input = form.Find("input");
            Assert.Contains("formidable-invalid", input.GetAttribute("class"));
            Assert.Equal("true", input.GetAttribute("aria-invalid"));
            Assert.Contains(form.FindAll("ul.formidable-message-list li"), li => li.TextContent.Contains("10"));
            Assert.Contains(form.FindAll("ul.formidable-summary__group--error li"), li => li.TextContent.Contains("10"));
        });

        await Services.DisposeAsync();
    }

    // While held, a field that passes wears formidable-valid, and one that fails wears no state
    // class at all. Mutation: delete the hold condition in LiveViewOf, or have the input register
    // with false for the hold, and the failing value's empty class attribute fails.
    [Fact]
    public async Task A_held_field_wears_valid_when_it_passes_and_no_state_class_when_it_fails()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var form = RenderForm(order, waitForSubmit: true);

        await form.InvokeAsync(() => form.Find("input").Change("ok"));
        form.WaitForAssertion(() => Assert.Contains("formidable-valid", form.Find("input").GetAttribute("class")));

        await form.InvokeAsync(() => form.Find("input").Change(TooLong));
        await Settle(form);
        form.WaitForAssertion(() => Assert.Equal(string.Empty, form.Find("input").GetAttribute("class") ?? string.Empty));

        await Services.DisposeAsync();
    }

    // While held, the pending class still shows as the field's own check runs, and the answer it
    // gives is then held. Mutation: delete the hold condition in LiveViewOf, or have the input
    // register with false for the hold, and the last assertion fails.
    [Fact]
    public async Task A_held_field_wears_pending_while_its_check_runs()
    {
        UseServices<GatedValidator>();
        var validator = (GatedValidator)Services.GetRequiredService<FluentValidation.IValidator<EngineOrder>>();
        var order = new EngineOrder();
        var form = RenderForm(order, waitForSubmit: true);

        await form.InvokeAsync(() => form.Find("input").Change("abc"));
        form.WaitForAssertion(() => Assert.Contains("formidable-pending", form.Find("input").GetAttribute("class")));

        await form.InvokeAsync(() => validator.Gate.SetResult());

        form.WaitForAssertion(() =>
        {
            var classes = form.Find("input").GetAttribute("class") ?? string.Empty;
            Assert.DoesNotContain("formidable-pending", classes);
            Assert.DoesNotContain("formidable-invalid", classes);
            Assert.Empty(form.FindAll("ul.formidable-message-list li"));
        });

        await Services.DisposeAsync();
    }

    // A plain input has put a live error on screen, then a held input for the same field mounts.
    // The field is now held, and every surface has to drop the error without waiting for a pass
    // (the refresh is switched off, so none is coming). Mutations: make the engine's held-state
    // handler do nothing, and the store keeps the message; delete the hold condition in
    // LiveViewOf, and the last assertion block fails.
    [Fact]
    public async Task A_held_input_mounting_beside_a_showing_error_retracts_it_from_every_surface()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<HeldBesidePlainHost>(0);
            builder.AddComponentParameter(1, nameof(HeldBesidePlainHost.Order), order);
            builder.AddComponentParameter(2, nameof(HeldBesidePlainHost.ShowHeld), false);
            builder.CloseComponent();
        });
        var host = cut.FindComponent<HeldBesidePlainHost>();
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await form.InvokeAsync(() => form.Find("input[data-name=plain]").Change(TooLong));
        form.WaitForAssertion(() =>
        {
            Assert.Contains(form.FindAll("ul.formidable-summary__group--error li"), li => li.TextContent.Contains("10"));
            Assert.Contains(form.FindAll("ul.formidable-message-list li"), li => li.TextContent.Contains("10"));
            var plain = form.Find("input[data-name=plain]");
            Assert.Contains("formidable-invalid", plain.GetAttribute("class"));
            Assert.Equal("true", plain.GetAttribute("aria-invalid"));
        });

        host.Render(parameters => parameters.Add(p => p.ShowHeld, true));

        form.WaitForAssertion(() =>
        {
            Assert.Empty(form.FindAll("ul.formidable-summary__group--error li"));
            Assert.Empty(form.FindAll("ul.formidable-message-list li"));
            Assert.Empty(form.Instance.Engine!.EditContext.GetValidationMessages(description));
            Assert.Equal(2, form.FindAll("input").Count);
            foreach (var input in form.FindAll("input"))
            {
                Assert.DoesNotContain("formidable-invalid", input.GetAttribute("class") ?? string.Empty);
                Assert.Null(input.GetAttribute("aria-invalid"));
            }
        });

        await Services.DisposeAsync();
    }

    // The reverse of the mount above: the held input leaves while the plain one stays. Its wait
    // stands until the rendered-field-set reconcile the form runs after the render, which drops no
    // field here and then ends the wait. The refresh is off, and that reconcile publishes nothing
    // else on the default policy, so the store moves only through the wait's own report. Mutation:
    // make the engine's held-state handler do nothing, and the edit context never gets the message.
    [Fact]
    public async Task A_held_input_leaving_beside_a_plain_one_shows_the_error_it_held()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<HeldBesidePlainHost>(0);
            builder.AddComponentParameter(1, nameof(HeldBesidePlainHost.Order), order);
            builder.AddComponentParameter(2, nameof(HeldBesidePlainHost.ShowHeld), true);
            builder.CloseComponent();
        });
        var host = cut.FindComponent<HeldBesidePlainHost>();
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();
        var engine = form.Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await form.InvokeAsync(() => form.Find("input[data-name=plain]").Change(TooLong));
        await Settle(form);
        AssertQuiet(cut, engine.EditContext, description);

        host.Render(parameters => parameters.Add(p => p.ShowHeld, false));

        form.WaitForAssertion(() =>
        {
            Assert.Single(form.FindAll("input"));
            AssertShowing(cut, engine.EditContext, description, nativeMessage: false);
        });

        await Services.DisposeAsync();
    }

    // ResetAsync builds a new engine, so the hold starts again. Mutation: delete the hold
    // condition in LiveViewOf, or have the input register with false for the hold, and the
    // assertions after the reset fail.
    [Fact]
    public async Task ResetAsync_starts_the_hold_again()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var form = RenderForm(order, waitForSubmit: true);

        await form.InvokeAsync(() => form.Find("input").Change(TooLong));
        await form.InvokeAsync(() => form.Find("form").Submit());
        form.WaitForAssertion(() => Assert.NotEmpty(form.FindAll("ul.formidable-message-list li")));

        await form.InvokeAsync(() => form.Instance.ResetAsync());
        form.WaitForAssertion(() => Assert.False(form.Instance.Engine!.HasSubmitted));

        await form.InvokeAsync(() => form.Find("input").Change(TooLong + "y"));
        await Settle(form);

        Assert.True(form.Instance.Engine!.GetFieldState(new FieldIdentifier(order, nameof(EngineOrder.Description))).IsModified);
        Assert.Empty(form.FindAll("ul.formidable-message-list li"));
        Assert.DoesNotContain("formidable-invalid", form.Find("input").GetAttribute("class") ?? string.Empty);

        await Services.DisposeAsync();
    }

    // A new Model builds a new engine, so the hold starts again, as it does after ResetAsync.
    // Mutations: delete the hold condition in LiveViewOf, or have the input register with false
    // for the hold, and the assertions after the swap fail.
    [Fact]
    public async Task A_new_model_starts_the_hold_again()
    {
        UseServices<EngineOrderValidator>();
        var first = new EngineOrder();
        RenderFragment<FormidableFormContext> firstContent = _ => inner => AddSurfaces(inner, this, first, waitForSubmit: true);
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", first);
            builder.AddComponentParameter(2, "ChildContent", firstContent);
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();

        await form.InvokeAsync(() => form.Find("input").Change(TooLong));
        await form.InvokeAsync(() => form.Find("form").Submit());
        form.WaitForAssertion(() => Assert.NotEmpty(form.FindAll("ul.formidable-message-list li")));
        Assert.True(form.Instance.Engine!.HasSubmitted);

        var second = new EngineOrder();
        RenderFragment<FormidableFormContext> secondContent = _ => inner => AddSurfaces(inner, this, second, waitForSubmit: true);
        form.Render(parameters => parameters
            .Add(p => p.Model, second)
            .Add(p => p.ChildContent, secondContent));
        form.WaitForAssertion(() => Assert.False(form.Instance.Engine!.HasSubmitted));

        await form.InvokeAsync(() => form.Find("input").Change(TooLong + "y"));
        await Settle(form);

        Assert.True(form.Instance.Engine!.GetFieldState(new FieldIdentifier(second, nameof(EngineOrder.Description))).IsModified);
        Assert.Empty(form.FindAll("ul.formidable-message-list li"));
        Assert.Empty(form.FindAll("ul.formidable-summary__group--error li"));
        Assert.DoesNotContain("formidable-invalid", form.Find("input").GetAttribute("class") ?? string.Empty);

        await Services.DisposeAsync();
    }

    // Attach mode holds the same way: the validator sits inside a consumer's own EditForm, and its
    // submit lifts the hold. The live profile is Draft and the refresh is off, so after the submit
    // only the live view can report the new length (a live pass under the submit profile would
    // feed the submit view too). Mutations: delete the hold condition in the engine's LiveViewOf,
    // and the first WaitForAssertion fails; make that condition ignore HasSubmitted, and the last
    // one fails.
    [Fact]
    public async Task An_attach_mode_submit_lifts_the_hold_on_a_held_input()
    {
        UseAttachServices();

        var order = new EngineOrder();
        var options = new FormidableOptions
        {
            LiveProfile = ValidationProfile.Draft,
            RefreshDebounce = Timeout.InfiniteTimeSpan,
        };
        var cut = Render(builder =>
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, nameof(EditForm.Model), order);
            builder.AddComponentParameter(2, nameof(EditForm.OnSubmit), EventCallback.Factory.Create<EditContext>(this, _ => { }));
            builder.AddComponentParameter(3, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.Options), options);
                // The typed fragment is what Context= names in markup; the validator cascades to
                // its own content only.
                inner.AddComponentParameter(
                    2,
                    nameof(FormidableValidator<EngineOrder>.ChildContent),
                    (RenderFragment<FormidableFormContext>)(_ => nested => AddSurfaces(nested, this, order, waitForSubmit: true)));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var validator = cut.FindComponent<FormidableValidator<EngineOrder>>();
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await cut.InvokeAsync(() => cut.Find("input").Change(TooLong));
        await cut.InvokeAsync(() => { });
        Assert.True(validator.Instance.Engine!.GetFieldState(description).IsModified);

        cut.WaitForAssertion(() =>
        {
            var input = cut.Find("input");
            Assert.Empty(cut.FindAll("ul.formidable-message-list li"));
            Assert.Empty(cut.FindAll("ul.formidable-summary__group--error li"));
            Assert.DoesNotContain("formidable-invalid", input.GetAttribute("class") ?? string.Empty);
            Assert.Null(input.GetAttribute("aria-invalid"));
        });

        await cut.InvokeAsync(() => validator.Instance.ValidateForSubmitAsync());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(cut.FindAll("ul.formidable-message-list li"), li => li.TextContent.Contains("10"));
            Assert.Contains(cut.FindAll("ul.formidable-summary__group--error li"), li => li.TextContent.Contains("10"));
        });

        await cut.InvokeAsync(() => cut.Find("input").Change(TooLong + "yy"));

        cut.WaitForAssertion(() =>
            Assert.Contains(cut.FindAll("ul.formidable-message-list li"), li => li.TextContent.Contains("13")));

        await Services.DisposeAsync();
    }

    // The wait and the commit event are separate settings: under OnInput every keystroke commits
    // and engages, and the field still waits. Mutation: derive the hold from UpdateOn (hold only
    // under OnChange), and the input holds nothing here.
    [Fact]
    public async Task WaitForSubmit_holds_under_OnInput_while_each_keystroke_commits()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var form = RenderForm(order, waitForSubmit: true, updateOn: InputUpdateMode.OnInput);
        var engine = form.Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await form.InvokeAsync(() => form.Find("input").Input(TooLong));
        await Settle(form);

        Assert.Equal(TooLong, order.Description);
        Assert.True(engine.GetFieldState(description).IsModified);
        AssertQuiet(form, engine.EditContext, description);

        await form.InvokeAsync(() => form.Find("form").Submit());

        form.WaitForAssertion(() => AssertShowing(form, engine.EditContext, description, nativeMessage: false));

        await Services.DisposeAsync();
    }

    // The same input instance, re-rendered by the host that renders the form: the showing error
    // leaves every surface as the wait starts. Mutation: skip ChangeHold in OnParametersSet, and
    // the error stays.
    [Fact]
    public async Task Switching_WaitForSubmit_on_hides_a_showing_error_at_the_next_render()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<WaitSwitchHost>(0);
            builder.AddComponentParameter(1, nameof(WaitSwitchHost.Order), order);
            builder.AddComponentParameter(2, nameof(WaitSwitchHost.Wait), false);
            builder.CloseComponent();
        });
        var host = cut.FindComponent<WaitSwitchHost>();
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var input = cut.FindComponent<FormidableInputText>().Instance;

        await cut.InvokeAsync(() => cut.Find("input").Change(TooLong));
        cut.WaitForAssertion(() => AssertShowing(cut, engine.EditContext, description, nativeMessage: true));

        host.Render(parameters => parameters.Add(p => p.Wait, true));

        cut.WaitForAssertion(() => AssertQuiet(cut, engine.EditContext, description));
        Assert.Same(input, cut.FindComponent<FormidableInputText>().Instance);
        Assert.False(engine.HasSubmitted);

        await Services.DisposeAsync();
    }

    // The reverse, with no new commit: the verdict filed while the field waited shows as the wait
    // ends. Mutation: skip ChangeHold in OnParametersSet, and every surface stays empty.
    [Fact]
    public async Task Switching_WaitForSubmit_off_shows_the_held_error_at_the_next_render()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<WaitSwitchHost>(0);
            builder.AddComponentParameter(1, nameof(WaitSwitchHost.Order), order);
            builder.AddComponentParameter(2, nameof(WaitSwitchHost.Wait), true);
            builder.CloseComponent();
        });
        var host = cut.FindComponent<WaitSwitchHost>();
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));
        var input = cut.FindComponent<FormidableInputText>().Instance;

        await cut.InvokeAsync(() => cut.Find("input").Change(TooLong));
        await Settle(cut);
        Assert.True(engine.GetFieldState(description).IsModified);
        AssertQuiet(cut, engine.EditContext, description);

        host.Render(parameters => parameters.Add(p => p.Wait, false));

        cut.WaitForAssertion(() => AssertShowing(cut, engine.EditContext, description, nativeMessage: true));
        Assert.Same(input, cut.FindComponent<FormidableInputText>().Instance);

        await Services.DisposeAsync();
    }

    // Only the nested component re-renders, so nothing but the wait's own report reaches the
    // message list, the summary and the native message outside it. Mutation: drop the engine's
    // HeldStateChanged subscription, and those surfaces stay stale in both directions.
    [Fact]
    public async Task Switching_WaitForSubmit_inside_a_nested_component_reaches_every_surface()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<NestedInputHost>(0);
            builder.AddComponentParameter(1, nameof(NestedInputHost.Order), order);
            builder.CloseComponent();
        });
        var nested = cut.FindComponent<NestedInput>();
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await cut.InvokeAsync(() => cut.Find("input").Change(TooLong));
        cut.WaitForAssertion(() => AssertShowing(cut, engine.EditContext, description, nativeMessage: true));

        nested.Render(parameters => parameters.Add(p => p.Wait, true));
        cut.WaitForAssertion(() => AssertQuiet(cut, engine.EditContext, description));

        nested.Render(parameters => parameters.Add(p => p.Wait, false));
        cut.WaitForAssertion(() => AssertShowing(cut, engine.EditContext, description, nativeMessage: true));

        await Services.DisposeAsync();
    }

    // A nested render mounts a waiting input beside a plain one that shows an error, and the form
    // does not re-render. The reconcile the mount posts drops no verdict, so it publishes nothing,
    // and the retraction is the wait's own report. Mutation: drop the engine's HeldStateChanged
    // subscription, and the native message, the inline list and the summary keep the error.
    [Fact]
    public async Task A_held_input_mounted_by_a_nested_render_retracts_the_native_message()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<NestedInputHost>(0);
            builder.AddComponentParameter(1, nameof(NestedInputHost.Order), order);
            builder.AddComponentParameter(2, nameof(NestedInputHost.PlainOutside), true);
            builder.CloseComponent();
        });
        var nested = cut.FindComponent<NestedInput>();
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await cut.InvokeAsync(() => cut.Find("input[data-name=plain]").Change(TooLong));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(cut.FindAll("div.validation-message"), div => div.TextContent.Contains("10"));
            Assert.Contains(cut.FindAll("ul.formidable-summary__group--error li"), li => li.TextContent.Contains("10"));
        });

        nested.Render(parameters => parameters
            .Add(p => p.Show, true)
            .Add(p => p.Wait, true));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, cut.FindAll("input").Count);
            AssertQuiet(cut, engine.EditContext, description);
        });

        await Services.DisposeAsync();
    }

    // A pin of the hold standing: the only waiting input for the field leaves through a nested
    // render, so the field has departed. Its wait stands until the reconcile the removal posts,
    // which drops the field before it settles the wait. So in the turn before that reconcile, the
    // engine's own reads and the edit context show nothing, and neither does any surface after it.
    // Mutation: end the hold inside Unregister, and the engine's issue read gives the message
    // inside the turn.
    [Fact]
    public async Task A_waiting_input_leaving_as_its_field_s_only_registration_shows_nothing()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<NestedInputHost>(0);
            builder.AddComponentParameter(1, nameof(NestedInputHost.Order), order);
            builder.CloseComponent();
        });
        var nested = cut.FindComponent<NestedInput>();
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        nested.Render(parameters => parameters.Add(p => p.Wait, true));
        await cut.InvokeAsync(() => cut.Find("input").Change(TooLong));
        await Settle(cut);
        Assert.True(engine.GetFieldState(description).IsModified);
        AssertQuiet(cut, engine.EditContext, description);

        var heldInTheTurn = true;
        var issuesInTheTurn = -1;
        await cut.InvokeAsync(() =>
        {
            nested.Render(parameters => parameters.Add(p => p.Show, false));
            heldInTheTurn = !engine.EditContext.GetValidationMessages(description).Any();
            issuesInTheTurn = engine.GetIssues(description).Count;
        });
        await Settle(cut);

        Assert.True(heldInTheTurn);
        Assert.Equal(0, issuesInTheTurn);
        Assert.Empty(cut.FindAll("input"));
        Assert.False(engine.Registry.IsRegistered(description));
        AssertQuiet(cut, engine.EditContext, description);

        await Services.DisposeAsync();
    }

    // The same departure, then an edit to another field, with nothing re-rendering the form. That
    // edit's check answers every field still engaged. Two things keep the departed field quiet:
    // the reconcile the removal posts drops the field, and until that reconcile runs the wait
    // stands. Mutation: drop FormidableForm's Registry.Changed subscription and end the hold
    // inside Unregister together, and the store, the summary and the native message all show it.
    [Fact]
    public async Task A_waiting_input_hidden_by_a_nested_render_stays_quiet_through_an_unrelated_edit()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var cut = Render(builder =>
        {
            builder.OpenComponent<NestedInputHost>(0);
            builder.AddComponentParameter(1, nameof(NestedInputHost.Order), order);
            builder.CloseComponent();
        });
        var nested = cut.FindComponent<NestedInput>();
        var host = cut.FindComponent<NestedInputHost>().Instance;
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        nested.Render(parameters => parameters.Add(p => p.Wait, true));
        await cut.InvokeAsync(() => cut.Find("input").Change(TooLong));
        await Settle(cut);
        AssertQuiet(cut, engine.EditContext, description);

        var formRenders = host.ContentRenders;
        nested.Render(parameters => parameters.Add(p => p.Show, false));
        await Settle(cut);

        await cut.InvokeAsync(() => engine.EditContext.NotifyFieldChanged(
            new FieldIdentifier(order, nameof(EngineOrder.Customer))));
        await Settle(cut);

        Assert.Equal(formRenders, host.ContentRenders);
        Assert.Empty(engine.EditContext.GetValidationMessages(description));
        Assert.DoesNotContain(cut.FindAll("ul.formidable-summary__group--error li"), li => li.TextContent.Contains("10"));
        Assert.DoesNotContain(cut.FindAll("div.validation-message"), div => div.TextContent.Contains("10"));

        await Services.DisposeAsync();
    }

    // Attach mode, both ways, the host re-rendering the consumer's EditForm. Mutation: drop the
    // engine's HeldStateChanged subscription, and the edit context keeps the message as the wait
    // starts.
    [Fact]
    public async Task Switching_WaitForSubmit_under_FormidableValidator_reaches_every_surface()
    {
        UseAttachServices();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<AttachWaitSwitchHost>(0);
            builder.AddComponentParameter(1, nameof(AttachWaitSwitchHost.Order), order);
            builder.AddComponentParameter(2, nameof(AttachWaitSwitchHost.Wait), false);
            builder.CloseComponent();
        });
        var host = cut.FindComponent<AttachWaitSwitchHost>();
        var engine = cut.FindComponent<FormidableValidator<EngineOrder>>().Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await cut.InvokeAsync(() => cut.Find("input").Change(TooLong));
        cut.WaitForAssertion(() => AssertShowing(cut, engine.EditContext, description, nativeMessage: true));

        host.Render(parameters => parameters.Add(p => p.Wait, true));
        cut.WaitForAssertion(() => AssertQuiet(cut, engine.EditContext, description));

        host.Render(parameters => parameters.Add(p => p.Wait, false));
        cut.WaitForAssertion(() => AssertShowing(cut, engine.EditContext, description, nativeMessage: true));

        await Services.DisposeAsync();
    }

    // A control FormidableField wraps waits through the context it is handed: the issue count,
    // aria-invalid and the class in its attributes all stay quiet, and so do the message list and
    // the summary. Mutation: have FormidableField register without the hold, and the count reads 1.
    [Fact]
    public async Task A_FormidableField_that_waits_for_submit_holds_what_its_context_carries()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableField<string>>(0);
                inner.AddComponentParameter(1, "For", (Expression<Func<string>>)(() => order.Description));
                inner.AddComponentParameter(2, "WaitForSubmit", true);
                inner.AddComponentParameter(3, "ChildContent", (RenderFragment<FormidableFieldContext>)(field => content =>
                {
                    content.OpenElement(0, "input");
                    content.AddMultipleAttributes(1, field.InputAttributes);
                    content.AddAttribute(2, "data-issues", field.Issues.Count.ToString(CultureInfo.InvariantCulture));
                    content.AddAttribute(3, "value", order.Description);
                    content.AddAttribute(4, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
                    {
                        order.Description = (string?)e.Value ?? string.Empty;
                        field.NotifyChanged();
                    }));
                    content.CloseElement();
                }));
                inner.CloseComponent();

                inner.OpenComponent<FormidableFieldMessage<string>>(4);
                inner.AddComponentParameter(5, "For", (Expression<Func<string>>)(() => order.Description));
                inner.CloseComponent();

                inner.OpenComponent<FormidableSummary>(6);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();
        var engine = form.Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await cut.InvokeAsync(() => cut.Find("input").Change(TooLong));
        await Settle(form);

        Assert.True(engine.GetFieldState(description).IsModified);
        Assert.Equal("0", cut.Find("input").GetAttribute("data-issues"));
        AssertQuiet(cut, engine.EditContext, description);

        await cut.InvokeAsync(() => cut.Find("form").Submit());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("1", cut.Find("input").GetAttribute("data-issues"));
            AssertShowing(cut, engine.EditContext, description, nativeMessage: false);
        });

        await Services.DisposeAsync();
    }

    // A native input speaks for the field through an anchor, and the anchor's wait holds the native
    // surfaces: the ValidationMessage, the class the edit context's provider computes, and the
    // summary. Mutation: have the anchor register without the hold, and all three show at once.
    [Fact]
    public async Task A_FormidableFieldAnchor_that_waits_for_submit_holds_the_native_surfaces()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<InputText>(0);
                inner.AddComponentParameter(1, "Value", order.Description);
                inner.AddComponentParameter(2, "ValueChanged",
                    EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty));
                inner.AddComponentParameter(3, "ValueExpression", (Expression<Func<string?>>)(() => order.Description));
                inner.CloseComponent();

                AddNativeMessage(inner, 4, order);

                inner.OpenComponent<FormidableFieldAnchor<string>>(6);
                inner.AddComponentParameter(7, "For", (Expression<Func<string>>)(() => order.Description));
                inner.AddComponentParameter(8, "WaitForSubmit", true);
                inner.CloseComponent();

                inner.OpenComponent<FormidableSummary>(9);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<EngineOrder>>();
        var engine = form.Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await cut.InvokeAsync(() => cut.Find("input").Change(TooLong));
        await Settle(form);

        Assert.True(engine.GetFieldState(description).IsModified);
        Assert.Empty(cut.FindAll("div.validation-message"));
        Assert.DoesNotContain("formidable-invalid", cut.Find("input").GetAttribute("class") ?? string.Empty);
        Assert.Empty(cut.FindAll("ul.formidable-summary__group--error li"));

        await cut.InvokeAsync(() => cut.Find("form").Submit());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(cut.FindAll("div.validation-message"), div => div.TextContent.Contains("10"));
            Assert.Contains("formidable-invalid", cut.Find("input").GetAttribute("class"));
            Assert.Contains(cut.FindAll("ul.formidable-summary__group--error li"), li => li.TextContent.Contains("10"));
        });

        await Services.DisposeAsync();
    }

    // The list's own rule, on a list a FormidableField edits through its context and a waiting
    // collection message shows: emptying the list engages the field, and its message waits. The
    // field wrapper registers the same field plain, so the wait holds because one registration
    // asks. Mutation: have the collection message register without the hold, and the message
    // shows at once.
    [Fact]
    public async Task A_FormidableCollectionMessage_that_waits_for_submit_holds_the_collection_field()
    {
        UseServices<EngineOrderValidator>();
        var only = new RowListItem();
        var model = new RowListModel { Items = [only] };
        FormidableFieldContext? captured = null;
        Expression<Func<IList<RowListItem>>> items = () => model.Items;
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<RowListModel>>(0);
            builder.AddComponentParameter(1, "Model", model);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableField<IList<RowListItem>>>(0);
                inner.AddComponentParameter(1, "For", items);
                inner.AddComponentParameter(2, "ChildContent", (RenderFragment<FormidableFieldContext>)(field => _ignored => captured = field));
                inner.CloseComponent();

                inner.OpenComponent<FormidableCollectionMessage<IList<RowListItem>>>(3);
                inner.AddComponentParameter(4, "For", items);
                inner.AddComponentParameter(5, "WaitForSubmit", true);
                inner.CloseComponent();

                inner.OpenComponent<FormidableSummary>(6);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<RowListModel>>();
        var engine = form.Instance.Engine!;
        var itemsField = new FieldIdentifier(model, nameof(RowListModel.Items));

        Assert.True(await form.InvokeAsync(() => captured!.RemoveItem(model.Items, only)));

        Assert.Empty(model.Items);
        Assert.True(engine.EditContext.IsModified(itemsField));
        Assert.Empty(cut.FindAll("ul.formidable-message-list li"));
        Assert.Empty(cut.FindAll("ul.formidable-summary__group--error li"));
        Assert.Empty(engine.EditContext.GetValidationMessages(itemsField));

        await cut.InvokeAsync(() => cut.Find("form").Submit());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(cut.FindAll("ul.formidable-message-list li"), li => li.TextContent == "Add at least one item");
            Assert.Contains(cut.FindAll("ul.formidable-summary__group--error li"), li => li.TextContent.Contains("Add at least one item"));
        });

        await Services.DisposeAsync();
    }

    // A kept row whose wait was switched off while it was away comes back answering live. The
    // retention it left held; the row re-created without the wait registers plain, which starts
    // a new visit, and a registration standing speaks for the field. Mutation: have IsHeld count
    // the retention while a registration stands and never forget the kept-hold count when a visit
    // starts, together, and every surface stays empty.
    [Fact]
    public async Task A_KeepRegistered_row_recreated_after_its_hold_was_switched_off_answers_live()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<KeptRowHost>(0);
            builder.AddComponentParameter(1, nameof(KeptRowHost.Order), order);
            builder.AddComponentParameter(2, nameof(KeptRowHost.Show), true);
            builder.AddComponentParameter(3, nameof(KeptRowHost.Wait), true);
            builder.CloseComponent();
        });
        var host = cut.FindComponent<KeptRowHost>();
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await cut.InvokeAsync(() => cut.Find("input").Change(TooLong));
        await Settle(cut);
        AssertQuiet(cut, engine.EditContext, description);

        host.Render(parameters => parameters.Add(p => p.Show, false));
        Assert.Empty(cut.FindAll("input"));
        Assert.True(engine.Registry.IsRegistered(description));
        AssertQuiet(cut, engine.EditContext, description);

        host.Render(parameters => parameters.Add(p => p.Wait, false));
        host.Render(parameters => parameters.Add(p => p.Show, true));

        // The plain registration ends the retention's hold, and that change re-renders the input
        // from a round posted past the render batch; it runs before the input is found.
        await Settle(cut);
        await cut.InvokeAsync(() => cut.Find("input").Change(TooLong + "y"));

        cut.WaitForAssertion(() => AssertShowing(cut, engine.EditContext, description, nativeMessage: false));
        Assert.False(engine.HasSubmitted);

        // A pin: the row came back without its wait, which started a new visit, so the retention
        // it leaves on going away again carries no hold and the summary keeps the message.
        // Mutation: never forget the kept-hold count when a visit starts, and the retention holds.
        host.Render(parameters => parameters.Add(p => p.Show, false));
        await Settle(cut);

        Assert.Empty(cut.FindAll("input"));
        Assert.True(engine.Registry.IsRegistered(description));
        Assert.Contains(cut.FindAll("ul.formidable-summary__group--error li"), li => li.TextContent.Contains("10"));
        Assert.Contains(engine.EditContext.GetValidationMessages(description), m => m.Contains("10"));

        await Services.DisposeAsync();
    }

    // A waiting FormidableField wraps a plain kit input for the same field, and a nested render
    // removes both while the field holds a failing value. The renderer disposes the wrapper before
    // the input it wraps, so for the rest of that batch a plain registration stands. The wrapper's
    // hold stands until the reconcile the removal posts settles the field, and that reconcile drops
    // the departed field first. So inside the removing turn no surface outside the removed row
    // shows the message, and after the reconcile none does either. Mutation: end the hold inside
    // Unregister, and the edit context holds the message inside the turn.
    [Fact]
    public async Task A_waiting_wrapper_leaving_with_its_plain_input_shows_nothing_before_the_reconcile()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<WaitingWrapperHost>(0);
            builder.AddComponentParameter(1, nameof(WaitingWrapperHost.Order), order);
            builder.CloseComponent();
        });
        var row = cut.FindComponent<WaitingWrapperRow>();
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await cut.InvokeAsync(() => cut.Find("input[data-name=row]").Change(TooLong));
        await Settle(cut);
        Assert.True(engine.GetFieldState(description).IsModified);
        AssertNothingShows(cut, engine.EditContext, description);

        var storeInTheTurn = -1;
        var issuesInTheTurn = -1;
        var surfacesInTheTurn = -1;
        await cut.InvokeAsync(() =>
        {
            row.Render(parameters => parameters.Add(p => p.Show, false));
            storeInTheTurn = engine.EditContext.GetValidationMessages(description).Count();
            issuesInTheTurn = engine.GetIssues(description).Count;
            surfacesInTheTurn = SurfacesShowing(cut, "10");
        });

        Assert.Equal(0, storeInTheTurn);
        Assert.Equal(0, issuesInTheTurn);
        Assert.Equal(0, surfacesInTheTurn);

        await Settle(cut);

        Assert.Empty(cut.FindAll("input"));
        Assert.False(engine.Registry.IsRegistered(description));
        AssertNothingShows(cut, engine.EditContext, description);

        await Services.DisposeAsync();
    }

    // A kept waiting FormidableField around a kept plain kit input leaves the page, as a row does
    // when it scrolls out of a virtualized list, while its value fails. The wrapper is disposed
    // first, so the plain input is the last to end. The kept wait still counts, so the field stays
    // quiet after the row leaves, after the whole-form re-check RefreshDebounce arms has run, and
    // through an edit to another field. It is quiet when the row returns, and the first submit
    // shows the message. Mutation: take KeptHolds from the last registration alone, and every
    // surface shows the message once the row has left.
    [Fact]
    public async Task A_kept_waiting_row_leaving_stays_quiet_through_an_unrelated_edit()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        Services.AddSingleton<TimeProvider>(clock);
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<WaitingWrapperHost>(0);
            builder.AddComponentParameter(1, nameof(WaitingWrapperHost.Order), order);
            builder.AddComponentParameter(2, nameof(WaitingWrapperHost.Keep), true);
            builder.AddComponentParameter(3, nameof(WaitingWrapperHost.Options), new FormidableOptions());
            builder.CloseComponent();
        });
        var host = cut.FindComponent<WaitingWrapperHost>();
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await cut.InvokeAsync(() => cut.Find("input[data-name=row]").Change(TooLong));
        await Settle(cut);
        AssertNothingShows(cut, engine.EditContext, description);

        host.Render(parameters => parameters.Add(p => p.Show, false));
        await Settle(cut);
        Assert.Empty(cut.FindAll("input"));
        Assert.True(engine.Registry.IsRegistered(description));
        AssertNothingShows(cut, engine.EditContext, description);

        clock.Advance(TimeSpan.FromMilliseconds(800));
        await Settle(cut);
        await Settle(cut);
        AssertNothingShows(cut, engine.EditContext, description);

        await cut.InvokeAsync(() =>
        {
            order.Customer = new EngineCustomer();
            engine.EditContext.NotifyFieldChanged(new FieldIdentifier(order, nameof(EngineOrder.Customer)));
        });
        await Settle(cut);
        AssertNothingShows(cut, engine.EditContext, description);

        host.Render(parameters => parameters.Add(p => p.Show, true));
        await Settle(cut);
        Assert.Single(cut.FindAll("input"));
        AssertNothingShows(cut, engine.EditContext, description);

        await cut.InvokeAsync(() => cut.Find("form").Submit());
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(cut.FindAll("div.waiting-row ul.formidable-message-list li"), li => li.TextContent.Contains("10"));
            Assert.Contains(cut.FindAll("ul.formidable-summary__group--error li"), li => li.TextContent.Contains("10"));
            Assert.Contains(engine.EditContext.GetValidationMessages(description), m => m.Contains("10"));
        });

        await Services.DisposeAsync();
    }

    // The same kept waiting row, scrolled out of view and back twice before the form is first
    // submitted. Each return registers the waiting wrapper again, so the field
    // waits while the row is on screen, and the kept wait holds it while the row is away. The
    // first submit shows the message on the row. Mutation: take KeptHolds from the last
    // registration alone, and the message shows once the row is first away.
    [Fact]
    public async Task A_kept_waiting_row_scrolled_out_and_back_stays_quiet_until_the_first_submit()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<WaitingWrapperHost>(0);
            builder.AddComponentParameter(1, nameof(WaitingWrapperHost.Order), order);
            builder.AddComponentParameter(2, nameof(WaitingWrapperHost.Keep), true);
            builder.CloseComponent();
        });
        var host = cut.FindComponent<WaitingWrapperHost>();
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await cut.InvokeAsync(() => cut.Find("input[data-name=row]").Change(TooLong));
        await Settle(cut);
        AssertNothingShows(cut, engine.EditContext, description);

        for (var scroll = 0; scroll < 2; scroll++)
        {
            host.Render(parameters => parameters.Add(p => p.Show, false));
            await Settle(cut);
            Assert.Empty(cut.FindAll("input"));
            AssertNothingShows(cut, engine.EditContext, description);

            host.Render(parameters => parameters.Add(p => p.Show, true));
            await Settle(cut);
            Assert.Single(cut.FindAll("input"));
            AssertNothingShows(cut, engine.EditContext, description);
        }

        await cut.InvokeAsync(() => cut.Find("form").Submit());
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(cut.FindAll("div.waiting-row ul.formidable-message-list li"), li => li.TextContent.Contains("10"));
            var input = cut.Find("input[data-name=row]");
            Assert.Contains("formidable-invalid", input.GetAttribute("class"));
            Assert.Equal("true", input.GetAttribute("aria-invalid"));
        });

        await Services.DisposeAsync();
    }

    // Four rows mount waiting in one nested render, each over a field that is engaged and failing.
    // Each mount rewrites its own field's messages at once, and the two notifications go out once
    // for the whole batch, from a round posted past it. Mutation: raise both notifications for
    // every flip, or rebuild the whole message store for every flip, and each count is four.
    [Fact]
    public async Task Flipping_many_waiting_fields_in_one_batch_notifies_once()
    {
        UseServices<SkuLengthValidator>();
        var order = new EngineOrder { Items = [new(), new(), new(), new()] };
        var cut = Render(builder =>
        {
            builder.OpenComponent<WaitingRowsHost>(0);
            builder.AddComponentParameter(1, nameof(WaitingRowsHost.Order), order);
            builder.CloseComponent();
        });
        var block = cut.FindComponent<WaitingRowsBlock>();
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var skus = order.Items.Select(item => new FieldIdentifier(item, nameof(EngineItem.Sku))).ToList();

        await cut.InvokeAsync(() =>
        {
            for (var i = 0; i < skus.Count; i++)
            {
                order.Items[i].Sku = $"sku-{i}";
                engine.EditContext.NotifyFieldChanged(skus[i]);
            }
        });
        await Settle(cut);
        Assert.All(skus, sku => Assert.Single(engine.EditContext.GetValidationMessages(sku)));
        Assert.Equal(skus.Count, SurfacesShowing(cut, "SKU", "ul.formidable-summary__group--error li"));

        var validationStateChanges = 0;
        var stateChanges = 0;
        engine.EditContext.OnValidationStateChanged += (_, _) => validationStateChanges++;
        engine.StateChanged += (_, _) => stateChanges++;

        block.Render(parameters => parameters.Add(p => p.Show, true));
        await Settle(cut);
        await Settle(cut);

        Assert.Equal(skus.Count, cut.FindAll("div.waiting-row").Count);
        Assert.Equal(1, validationStateChanges);
        Assert.Equal(1, stateChanges);
        Assert.All(skus, sku => Assert.Empty(engine.EditContext.GetValidationMessages(sku)));
        Assert.Equal(0, SurfacesShowing(cut, "SKU"));

        await Services.DisposeAsync();
    }

    // A pin: a summary sits above a block of rows that mount waiting, each over a field a plain
    // input elsewhere already shows a live error for. Each mount rewrites its own
    // field's messages at once, so the row's own message list and native ValidationMessage, which
    // render after the mount in the same batch, show nothing. The summary rendered before the
    // mounts in that batch, and drops the entries at the round posted past it. Mutation: defer
    // the message rewrite into the posted round, and a mounting row's native ValidationMessage
    // shows its message inside the batch.
    [Fact]
    public async Task A_summary_above_waiting_rows_mounting_in_bulk_drops_their_entries()
    {
        UseServices<SkuLengthValidator>();
        var order = new EngineOrder { Items = [new(), new(), new()] };
        var cut = Render(builder =>
        {
            builder.OpenComponent<WaitingRowsHost>(0);
            builder.AddComponentParameter(1, nameof(WaitingRowsHost.Order), order);
            builder.AddComponentParameter(2, nameof(WaitingRowsHost.PlainInputs), true);
            builder.CloseComponent();
        });
        var block = cut.FindComponent<WaitingRowsBlock>();

        for (var i = 0; i < order.Items.Count; i++)
        {
            await cut.InvokeAsync(() => cut.Find($"input[data-name=plain-{i}]").Change($"sku-{i}"));
        }

        await Settle(cut);
        Assert.Equal(order.Items.Count, SurfacesShowing(cut, "SKU", "ul.formidable-summary__group--error li"));

        var rowsInTheBatch = -1;
        var rowSurfacesInTheBatch = -1;
        await cut.InvokeAsync(() =>
        {
            block.Render(parameters => parameters.Add(p => p.Show, true));
            rowsInTheBatch = cut.FindAll("div.waiting-row").Count;
            rowSurfacesInTheBatch = SurfacesShowing(
                cut, "SKU", "div.waiting-row ul.formidable-message-list li", "div.waiting-row div.validation-message");
        });

        Assert.Equal(order.Items.Count, rowsInTheBatch);
        Assert.Equal(0, rowSurfacesInTheBatch);

        await Settle(cut);

        Assert.Equal(0, SurfacesShowing(cut, "SKU", "ul.formidable-summary__group--error li"));

        await Services.DisposeAsync();
    }

    // A consumer's OnValidationStateChanged handler throws once, as a waiting input mounts over a
    // field that is engaged and failing with nothing else registered for it. The mount's
    // notification is posted past the render batch, so no consumer code runs while the input
    // registers, and the input holds its registration's handle. The throw comes from that post,
    // and the form hands it to the nearest ErrorBoundary above the form. The boundary here sits
    // inside the form, around the input, so it shows nothing, and the throw takes the renderer's
    // own unhandled path: bUnit completes Renderer.UnhandledException with it and rethrows it from
    // the next render call. Removing the input in that render still ends the registration.
    // Mutation: raise the two notifications inside the engine's held-state handler, and the throw
    // comes out of the input's mount instead, so the boundary around the input shows it and
    // nothing reaches the renderer's unhandled path.
    [Fact]
    public async Task A_throwing_validation_state_handler_during_a_waiting_mount_leaks_no_registration()
    {
        UseServices<EngineOrderValidator>();
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<BoundaryInputHost>(0);
            builder.AddComponentParameter(1, nameof(BoundaryInputHost.Order), order);
            builder.CloseComponent();
        });
        var host = cut.FindComponent<BoundaryInputHost>();
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;
        var description = new FieldIdentifier(order, nameof(EngineOrder.Description));

        await cut.InvokeAsync(() =>
        {
            order.Description = TooLong;
            engine.EditContext.NotifyFieldChanged(description);
        });
        await Settle(cut);
        Assert.NotEmpty(engine.EditContext.GetValidationMessages(description));
        Assert.False(engine.Registry.IsRegistered(description));

        var armed = true;
        engine.EditContext.OnValidationStateChanged += (_, _) =>
        {
            if (armed)
            {
                armed = false;
                throw new InvalidOperationException("a consumer handler throws");
            }
        };

        host.Render(parameters => parameters.Add(p => p.ShowInput, true));
        await Settle(cut);
        await Settle(cut);
        Assert.False(armed);
        Assert.Empty(cut.FindAll("p.section-failed"));
        Assert.True(Renderer.UnhandledException.IsCompleted);
        Assert.Equal("a consumer handler throws", (await Renderer.UnhandledException).Message);

        var rethrown = Record.Exception(() => host.Render(parameters => parameters.Add(p => p.ShowInput, false)));
        await Settle(cut);
        await Settle(cut);

        Assert.Equal("a consumer handler throws", Assert.IsType<InvalidOperationException>(rethrown).Message);
        Assert.Empty(cut.FindAll("input"));
        Assert.False(engine.Registry.IsRegistered(description));

        await Services.DisposeAsync();
    }

    /// <summary>Test-only host: a plain input and a summary always, and a held input on the same field while <see cref="ShowHeld"/> is set.</summary>
    private sealed class HeldBesidePlainHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool ShowHeld { get; set; }

        private readonly FormidableOptions _options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), _options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                AddHeldInput(inner, 0, this, Order, waitForSubmit: false, dataName: "plain");
                if (ShowHeld)
                {
                    AddHeldInput(inner, 10, this, Order, waitForSubmit: true, dataName: "held");
                }

                inner.OpenComponent<FormidableFieldMessage<string>>(18);
                inner.AddComponentParameter(19, "For", (Expression<Func<string>>)(() => Order.Description));
                inner.CloseComponent();

                inner.OpenComponent<FormidableSummary>(20);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only host that renders the form itself, so re-rendering it with a new <see cref="Wait"/> re-renders the same input with a new <c>WaitForSubmit</c>.</summary>
    private sealed class WaitSwitchHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool Wait { get; set; }

        private readonly FormidableOptions _options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), _options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                AddSurfaces(inner, this, Order, Wait);
                AddNativeMessage(inner, 20, Order);
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only host: a form whose content is a <see cref="NestedInput"/>, with the message list, the summary and a native message outside it, and a plain input beside it when <see cref="PlainOutside"/> is set.</summary>
    private sealed class NestedInputHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool PlainOutside { get; set; }

        private readonly FormidableOptions _options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };

        // How many times the form has rendered its content, which a nested component's own render
        // never does.
        public int ContentRenders { get; private set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), _options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                ContentRenders++;
                if (PlainOutside)
                {
                    AddHeldInput(inner, 0, this, Order, waitForSubmit: false, dataName: "plain");
                }

                inner.OpenComponent<NestedInput>(10);
                inner.AddComponentParameter(11, nameof(NestedInput.Order), Order);
                inner.AddComponentParameter(12, nameof(NestedInput.Show), !PlainOutside);
                inner.CloseComponent();

                inner.OpenComponent<FormidableFieldMessage<string>>(13);
                inner.AddComponentParameter(14, "For", (Expression<Func<string>>)(() => Order.Description));
                inner.CloseComponent();

                inner.OpenComponent<FormidableSummary>(15);
                inner.CloseComponent();

                AddNativeMessage(inner, 16, Order);
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only component inside the form that renders the description's input while <see cref="Show"/> is set, waiting while <see cref="Wait"/> is.</summary>
    private sealed class NestedInput : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool Show { get; set; }

        [Parameter]
        public bool Wait { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Show)
            {
                AddHeldInput(builder, 0, this, Order, Wait, dataName: "nested");
            }
        }
    }

    /// <summary>Test-only host for attach mode: a consumer's <c>EditForm</c> with a <c>FormidableValidator</c>, re-rendered with a new <see cref="Wait"/>.</summary>
    private sealed class AttachWaitSwitchHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool Wait { get; set; }

        private readonly FormidableOptions _options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, nameof(EditForm.Model), Order);
            builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableValidator<EngineOrder>>(0);
                inner.AddComponentParameter(1, nameof(FormidableValidator<EngineOrder>.Options), _options);
                inner.AddComponentParameter(
                    2,
                    nameof(FormidableValidator<EngineOrder>.ChildContent),
                    (RenderFragment<FormidableFormContext>)(_ => nested =>
                    {
                        AddSurfaces(nested, this, Order, Wait);
                        AddNativeMessage(nested, 20, Order);
                    }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only host: a kept input shown while <see cref="Show"/> is set and waiting while <see cref="Wait"/> is, with the message list and the summary outside it.</summary>
    private sealed class KeptRowHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool Show { get; set; }

        [Parameter]
        public bool Wait { get; set; }

        private readonly FormidableOptions _options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), _options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                if (Show)
                {
                    AddHeldInput(inner, 0, this, Order, Wait, keepRegistered: true);
                }

                inner.OpenComponent<FormidableFieldMessage<string>>(10);
                inner.AddComponentParameter(11, "For", (Expression<Func<string>>)(() => Order.Description));
                inner.CloseComponent();

                inner.OpenComponent<FormidableSummary>(12);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only host: a form whose content is a <see cref="WaitingWrapperRow"/>, with the description's message list, the kit summary, a native summary and a native message outside it.</summary>
    private sealed class WaitingWrapperHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool Show { get; set; } = true;

        [Parameter]
        public bool Keep { get; set; }

        [Parameter]
        public FormidableOptions Options { get; set; } = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), Options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<WaitingWrapperRow>(0);
                inner.AddComponentParameter(1, nameof(WaitingWrapperRow.Order), Order);
                inner.AddComponentParameter(2, nameof(WaitingWrapperRow.Show), Show);
                inner.AddComponentParameter(3, nameof(WaitingWrapperRow.Keep), Keep);
                inner.CloseComponent();

                inner.OpenComponent<FormidableFieldMessage<string>>(10);
                inner.AddComponentParameter(11, "For", (Expression<Func<string>>)(() => Order.Description));
                inner.CloseComponent();

                inner.OpenComponent<FormidableSummary>(12);
                inner.CloseComponent();

                inner.OpenComponent<ValidationSummary>(13);
                inner.CloseComponent();

                AddNativeMessage(inner, 14, Order);
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only row: a <c>FormidableField</c> with <c>WaitForSubmit</c> around a plain kit input and a message list for the description, both kept registered while <see cref="Keep"/> is set.</summary>
    private sealed class WaitingWrapperRow : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool Show { get; set; }

        [Parameter]
        public bool Keep { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (!Show)
            {
                return;
            }

            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "waiting-row");
            builder.OpenComponent<FormidableField<string>>(2);
            builder.AddComponentParameter(3, "For", (Expression<Func<string>>)(() => Order.Description));
            builder.AddComponentParameter(4, "WaitForSubmit", true);
            builder.AddComponentParameter(5, "KeepRegistered", Keep);
            builder.AddComponentParameter(6, "ChildContent", (RenderFragment<FormidableFieldContext>)(_ => content =>
            {
                AddHeldInput(content, 0, this, Order, waitForSubmit: false, dataName: "row", keepRegistered: Keep);

                content.OpenComponent<FormidableFieldMessage<string>>(10);
                content.AddComponentParameter(11, "For", (Expression<Func<string>>)(() => Order.Description));
                content.CloseComponent();
            }));
            builder.CloseComponent();
            builder.CloseElement();
        }
    }

    /// <summary>Test-only host: a kit summary above an optional plain input per item's SKU, then a <see cref="WaitingRowsBlock"/>, then a native summary.</summary>
    private sealed class WaitingRowsHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool PlainInputs { get; set; }

        private readonly FormidableOptions _options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), _options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                inner.CloseComponent();

                if (PlainInputs)
                {
                    for (var i = 0; i < Order.Items.Count; i++)
                    {
                        AddSkuInput(inner, 10, this, Order.Items[i], waitForSubmit: false, $"plain-{i}");
                    }
                }

                inner.OpenComponent<WaitingRowsBlock>(20);
                inner.AddComponentParameter(21, nameof(WaitingRowsBlock.Order), Order);
                inner.CloseComponent();

                inner.OpenComponent<ValidationSummary>(22);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>Test-only block: while <see cref="Show"/> is set, one row per item, each a waiting kit input for its SKU, a message list and a native message.</summary>
    private sealed class WaitingRowsBlock : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool Show { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (!Show)
            {
                return;
            }

            foreach (var item in Order.Items)
            {
                builder.OpenElement(0, "div");
                builder.SetKey(item);
                builder.AddAttribute(1, "class", "waiting-row");
                AddSkuInput(builder, 2, this, item, waitForSubmit: true, dataName: null);

                builder.OpenComponent<FormidableFieldMessage<string>>(10);
                builder.AddComponentParameter(11, "For", (Expression<Func<string>>)(() => item.Sku));
                builder.CloseComponent();

                builder.OpenComponent<ValidationMessage<string>>(12);
                builder.AddComponentParameter(13, "For", (Expression<Func<string>>)(() => item.Sku));
                builder.CloseComponent();
                builder.CloseElement();
            }
        }
    }

    // A kit input for one item's SKU, waiting or not. It takes six sequence numbers from sequence.
    private static void AddSkuInput(
        RenderTreeBuilder builder, int sequence, object receiver, EngineItem item, bool waitForSubmit, string? dataName)
    {
        builder.OpenComponent<FormidableInputText>(sequence);
        builder.SetKey(item);
        builder.AddComponentParameter(sequence + 1, "For", (Expression<Func<string?>>)(() => item.Sku));
        builder.AddComponentParameter(sequence + 2, "Value", item.Sku);
        builder.AddComponentParameter(sequence + 3, "ValueChanged",
            EventCallback.Factory.Create<string?>(receiver, v => item.Sku = v ?? string.Empty));
        builder.AddComponentParameter(sequence + 4, "WaitForSubmit", waitForSubmit);
        if (dataName is not null)
        {
            builder.AddComponentParameter(sequence + 5, "data-name", dataName);
        }

        builder.CloseComponent();
    }

    /// <summary>Test-only host: a form with a kit summary, and a waiting description input inside an <c>ErrorBoundary</c> while <see cref="ShowInput"/> is set.</summary>
    private sealed class BoundaryInputHost : ComponentBase
    {
        [Parameter]
        public EngineOrder Order { get; set; } = default!;

        [Parameter]
        public bool ShowInput { get; set; }

        private readonly FormidableOptions _options = new() { RefreshDebounce = Timeout.InfiniteTimeSpan };

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<EngineOrder>.Model), Order);
            builder.AddComponentParameter(2, nameof(FormidableForm<EngineOrder>.Options), _options);
            builder.AddComponentParameter(3, nameof(FormidableForm<EngineOrder>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                inner.OpenComponent<FormidableSummary>(0);
                inner.CloseComponent();

                inner.OpenComponent<ErrorBoundary>(1);
                inner.AddComponentParameter(2, nameof(ErrorBoundary.ChildContent), (RenderFragment)(section =>
                {
                    if (ShowInput)
                    {
                        AddHeldInput(section, 0, this, Order, waitForSubmit: true);
                    }
                }));
                inner.AddComponentParameter(3, nameof(ErrorBoundary.ErrorContent), (RenderFragment<Exception>)(_ => error =>
                    error.AddMarkupContent(0, "<p class=\"section-failed\">The section failed.</p>")));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>One rule per item: a SKU longer than three characters fails with a message that names it.</summary>
    private sealed class SkuLengthValidator : FluentValidation.AbstractValidator<EngineOrder>
    {
        public SkuLengthValidator() =>
            RuleForEach(x => x.Items).ChildRules(item =>
                item.RuleFor(x => x.Sku).MaximumLength(3).WithMessage(x => $"SKU {x.Sku} is too long"));
    }
}
