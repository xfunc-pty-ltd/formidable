using System.Linq.Expressions;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins how a kit input learns which field it drives — <c>@bind-Value</c> alone, an explicit
/// <c>For</c>, both together, or neither — and how it treats a consumer's own <c>onblur</c>
/// handler when it wants that event for itself.
/// </summary>
public class FormidableInputBindingTests : BunitContext
{
    public FormidableInputBindingTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    private IRenderedComponent<FormidableForm<EngineOrder>> RenderInput(
        EngineOrder order, params (string Name, object Value)[] parameters)
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<EngineOrder>>(0);
            builder.AddComponentParameter(1, "Model", order);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FormidableInputText>(0);
                var sequence = 1;
                foreach (var (name, value) in parameters)
                {
                    inner.AddComponentParameter(sequence++, name, value);
                }

                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        return cut.FindComponent<FormidableForm<EngineOrder>>();
    }

    private static string ExpectedId(EngineOrder order) =>
        FormidableFieldId.For(new FieldIdentifier(order, nameof(EngineOrder.Description)));

    // The whole point of ValueExpression: markup that says the field once. The rendered id proves
    // the field actually resolved (registration, aria and focus all key off it), and the live pass
    // proves the registration reached the engine rather than merely not throwing.
    [Fact]
    public void Bind_only_markup_resolves_the_field_and_validates()
    {
        var order = new EngineOrder();
        var cut = Render(builder =>
        {
            builder.OpenComponent<BoundInputHost>(0);
            builder.AddComponentParameter(1, nameof(BoundInputHost.Order), order);
            builder.CloseComponent();
        });

        Assert.Equal(ExpectedId(order), cut.Find("input").GetAttribute("id"));

        cut.Find("input").Change(new string('x', 11));

        Assert.Equal(new string('x', 11), order.Description);
        cut.WaitForAssertion(() => Assert.Contains("formidable-invalid", cut.Find("input").GetAttribute("class")));
    }

    [Fact]
    public void For_alone_still_resolves_the_field()
    {
        var order = new EngineOrder();
        var form = RenderInput(
            order,
            ("For", (Expression<Func<string?>>)(() => order.Description)),
            ("Value", order.Description));

        Assert.Equal(ExpectedId(order), form.Find("input").GetAttribute("id"));
    }

    // The copy-paste slip, made harmless-by-precedence rather than by warning: when both spellings
    // are present and disagree, For is the one that names the field. (Bind-only markup cannot
    // produce the disagreement at all, which is why it is the taught idiom.)
    [Fact]
    public void For_wins_when_it_disagrees_with_the_bound_expression()
    {
        var order = new EngineOrder { Customer = new EngineCustomer() };
        var form = RenderInput(
            order,
            ("For", (Expression<Func<string?>>)(() => order.Description)),
            ("ValueExpression", (Expression<Func<string?>>)(() => order.Customer!.Name)),
            ("Value", order.Description));

        Assert.Equal(ExpectedId(order), form.Find("input").GetAttribute("id"));
    }

    [Fact]
    public void Neither_spelling_names_the_component_and_shows_both()
    {
        var order = new EngineOrder();

        var exception = Assert.ThrowsAny<Exception>(() => RenderInput(order, ("Value", order.Description)));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("FormidableInputText", exception.Message);
        Assert.Contains("@bind-Value", exception.Message);
        Assert.Contains("For=", exception.Message);
    }

    // The chain contract: the consumer's handler runs first, the library's notification second.
    // A committed change precedes the blur so a notification is pending for it to deliver.
    // EditContext.OnFieldChanged is the library half's own event, so the recorded order is the
    // real dispatch order rather than a proxy for it.
    [Fact]
    public void A_splatted_onblur_runs_before_the_library_notifies_the_engine()
    {
        var order = new EngineOrder();
        var log = new List<string>();
        var form = RenderInput(
            order,
            ("For", (Expression<Func<string?>>)(() => order.Description)),
            ("Value", order.Description),
            ("UpdateOn", InputUpdateMode.OnBlur),
            ("onblur", EventCallback.Factory.Create<FocusEventArgs>(this, () => log.Add("consumer"))));

        form.Instance.Engine!.EditContext.OnFieldChanged += (_, _) => log.Add("library");

        form.Find("input").Change("committed");
        form.Find("input").Blur();

        form.WaitForAssertion(() => Assert.Equal(["consumer", "library"], log));
    }

    // A splat can carry any shape Blazor's event plumbing produces, and the chain has to invoke all
    // of them: @onblur in Razor markup compiles to an EventCallback<FocusEventArgs>, while markup
    // assembled by hand can pass the untyped EventCallback or a bare delegate of either arity.
    [Fact]
    public void Every_handler_shape_a_splatted_onblur_can_carry_is_invoked()
    {
        var log = new List<string>();
        (string Name, Func<Action, object> Build)[] shapes =
        [
            ("EventCallback<FocusEventArgs>", record => EventCallback.Factory.Create<FocusEventArgs>(this, record)),
            ("EventCallback", record => EventCallback.Factory.Create(this, record)),
            ("Func<FocusEventArgs, Task>", record => (Func<FocusEventArgs, Task>)(_ =>
            {
                record();
                return Task.CompletedTask;
            })),
            ("Func<Task>", record => (Func<Task>)(() =>
            {
                record();
                return Task.CompletedTask;
            })),
            ("Action<FocusEventArgs>", record => (Action<FocusEventArgs>)(_ => record())),
            ("Action", record => record),
        ];

        foreach (var (name, build) in shapes)
        {
            var order = new EngineOrder();
            var form = RenderInput(
                order,
                ("For", (Expression<Func<string?>>)(() => order.Description)),
                ("Value", order.Description),
                ("UpdateOn", InputUpdateMode.OnBlur),
                ("onblur", build(() => log.Add(name))));

            form.Find("input").Blur();
        }

        Assert.Equal(shapes.Select(shape => shape.Name), log);
    }

    // The same chain, reached through the other shapes a splat can carry: a plain delegate rather
    // than the EventCallback the Razor compiler emits for @onblur.
    [Fact]
    public void A_splatted_onblur_delegate_is_invoked_and_the_live_pass_still_runs()
    {
        var order = new EngineOrder();
        var log = new List<string>();
        var form = RenderInput(
            order,
            ("For", (Expression<Func<string?>>)(() => order.Description)),
            ("Value", order.Description),
            ("ValueChanged", EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty)),
            ("UpdateOn", InputUpdateMode.OnBlur),
            ("onblur", (Action)(() => log.Add("consumer"))));

        form.Find("input").Change(new string('x', 11));
        form.Find("input").Blur();

        Assert.Equal(["consumer"], log);
        form.WaitForAssertion(() => Assert.Contains("formidable-invalid", form.Find("input").GetAttribute("class")));
    }

    // A committed change precedes the blur so a notification is pending — the property under
    // test is that the delivery waits for the consumer's handler to complete, not merely start.
    [Fact]
    public async Task A_splatted_onblur_task_delegate_is_awaited_before_the_engine_is_notified()
    {
        var order = new EngineOrder();
        var log = new List<string>();
        var gate = new TaskCompletionSource();
        var form = RenderInput(
            order,
            ("For", (Expression<Func<string?>>)(() => order.Description)),
            ("Value", order.Description),
            ("UpdateOn", InputUpdateMode.OnBlur),
            ("onblur", (Func<Task>)(async () =>
            {
                await gate.Task;
                log.Add("consumer");
            })));

        form.Instance.Engine!.EditContext.OnFieldChanged += (_, _) => log.Add("library");

        form.Find("input").Change("committed");
        var blur = form.Find("input").BlurAsync(new FocusEventArgs());
        Assert.Empty(log);

        gate.SetResult();
        await blur;

        Assert.Equal(["consumer", "library"], log);
    }

    // A string splat is an ordinary HTML attribute value, not a .NET handler — there is nothing to
    // chain, so the library's own handler is all that runs and the commit must survive it.
    [Fact]
    public void A_splatted_onblur_string_leaves_the_library_handler_alone()
    {
        var order = new EngineOrder();
        var form = RenderInput(
            order,
            ("For", (Expression<Func<string?>>)(() => order.Description)),
            ("Value", order.Description),
            ("ValueChanged", EventCallback.Factory.Create<string?>(this, v => order.Description = v ?? string.Empty)),
            ("UpdateOn", InputUpdateMode.OnBlur),
            ("onblur", "console.log('blur')"));

        form.Find("input").Change(new string('x', 11));
        form.Find("input").Blur();

        form.WaitForAssertion(() => Assert.Contains("formidable-invalid", form.Find("input").GetAttribute("class")));
    }

    // Under the two combined modes the library renders no onblur of its own, so a splatted handler
    // is the only one on the element and reaches the DOM untouched.
    [Fact]
    public void A_splatted_onblur_passes_through_untouched_in_the_combined_modes()
    {
        var order = new EngineOrder();
        var log = new List<string>();
        var form = RenderInput(
            order,
            ("For", (Expression<Func<string?>>)(() => order.Description)),
            ("Value", order.Description),
            ("onblur", EventCallback.Factory.Create<FocusEventArgs>(this, () => log.Add("consumer"))));

        form.Find("input").Blur();

        Assert.Equal(["consumer"], log);
    }
}
