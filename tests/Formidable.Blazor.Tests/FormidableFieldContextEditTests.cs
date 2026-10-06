using System.Runtime.CompilerServices;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins <c>Edit</c> and <c>TryEdit</c> on <c>FormidableFieldContext</c>: each runs the page's own
/// edit, then reports the change for the field, so the check it starts reads the edited list.
/// <c>Edit</c> always reports; a <c>TryEdit</c> whose edit returns <see langword="false"/> reports
/// nothing, and so does an edit that throws, faults or is cancelled.
/// </summary>
// LiveDebounce stays at its default in every test here. With no wait, the live check starts
// inside the report itself, which is what lets a report made before the edit show.
public class FormidableFieldContextEditTests : BunitContext
{
    private const string AtLeastOne = "Add at least one item";

    public FormidableFieldContextEditTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<FluentValidation.IValidator<RowListModel>, RowListValidator>();
    }

    // The check reads the list when it runs, so the count message showing proves it read the
    // cleared list. Mutation that must break it: report before running the edit, and the check
    // reads the list with its item still in it, passes, and no message appears.
    [Fact]
    public async Task Edit_runs_the_edit_then_reports_the_change()
    {
        var model = new RowListModel { Items = [new RowListItem()] };
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);

        await form.InvokeAsync(() => context.Value!.Edit(() => model.Items.Clear()));

        Assert.Empty(model.Items);
        Assert.True(form.Instance.Engine!.EditContext.IsModified(ItemsField(model)));
        form.WaitForAssertion(() => Assert.Equal(AtLeastOne, form.Find("li").TextContent));
    }

    // A bool assignment converts to a Func<bool> as readily as to an Action, and C# prefers the
    // delegate with a return type, so Edit offers no Func<bool> for it to bind: the method's name
    // decides whether the change is reported, never the lambda's shape. The list starts empty, so
    // the engaged field's own rule answers with its message. Mutation that must break it: give
    // Edit a Func<bool> overload again beside Edit(Action), and the assignment of false binds it
    // and reports nothing.
    [Fact]
    public async Task Edit_with_a_bool_assignment_always_reports()
    {
        var host = RenderHost(listStartsEmpty: true);

        await host.Host.Find("#assign-false").ClickAsync(new());

        Assert.False(host.Model.Flag);
        Assert.True(host.IsModified());
        host.Host.WaitForAssertion(() => Assert.Equal(
            AtLeastOne,
            Assert.Single(host.Engine.GetIssues(ItemsField(host.Model))).Message));
    }

    // Mutations that must break it: report only when the edit returned false (the field is never
    // marked modified), and return the negation of what the edit returned (the Assert.True fails).
    [Fact]
    public async Task TryEdit_returning_true_reports_and_returns_true()
    {
        var only = new RowListItem();
        var model = new RowListModel { Items = [only] };
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);

        var result = await form.InvokeAsync(() => context.Value!.TryEdit(() => model.Items.Remove(only)));

        Assert.True(result);
        Assert.Empty(model.Items);
        Assert.True(form.Instance.Engine!.EditContext.IsModified(ItemsField(model)));
        form.WaitForAssertion(() => Assert.Equal(AtLeastOne, form.Find("li").TextContent));
    }

    // The list starts empty, so a report here would engage a field whose rule fails. Mutation
    // that must break it: report whatever the edit returned, and the field is marked modified.
    [Fact]
    public async Task TryEdit_returning_false_reports_nothing_and_returns_false()
    {
        var model = new RowListModel();
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);

        var result = await form.InvokeAsync(() => context.Value!.TryEdit(() => model.Items.Remove(new RowListItem())));

        Assert.False(result);
        Assert.False(form.Instance.Engine!.EditContext.IsModified(ItemsField(model)));
        Assert.Empty(form.FindAll("li"));
    }

    // Both synchronous methods, Edit and TryEdit. Mutation that must break it: report in a
    // finally, and the field is marked modified on the way out of the throwing edit.
    [Fact]
    public async Task Edit_that_throws_reports_nothing_and_rethrows()
    {
        var model = new RowListModel { Items = [new RowListItem()] };
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);
        Action throws = () => throw new InvalidOperationException("action");
        Func<bool> throwsBool = () => throw new InvalidOperationException("func");

        var fromAction = await Assert.ThrowsAsync<InvalidOperationException>(
            () => form.InvokeAsync(() => context.Value!.Edit(throws)));
        var fromFunc = await Assert.ThrowsAsync<InvalidOperationException>(
            () => form.InvokeAsync(() => context.Value!.TryEdit(throwsBool)));

        Assert.Equal("action", fromAction.Message);
        Assert.Equal("func", fromFunc.Message);
        Assert.False(form.Instance.Engine!.EditContext.IsModified(ItemsField(model)));
    }

    // Mutation that must break it: report before awaiting the edit, and the field is marked
    // modified while the gate is still closed.
    [Fact]
    public async Task An_async_edit_reports_after_it_completes()
    {
        var model = new RowListModel { Items = [new RowListItem()] };
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);
        var engine = form.Instance.Engine!;
        var gate = new TaskCompletionSource();
        var edit = Task.CompletedTask;

        await form.InvokeAsync(() =>
        {
            edit = context.Value!.Edit(async () =>
            {
                await gate.Task;
                model.Items.Clear();
            });
        });

        Assert.False(edit.IsCompleted);
        Assert.Single(model.Items);
        Assert.False(engine.EditContext.IsModified(ItemsField(model)));

        gate.SetResult();
        await edit;

        Assert.Empty(model.Items);
        Assert.True(engine.EditContext.IsModified(ItemsField(model)));
        form.WaitForAssertion(() => Assert.Equal(AtLeastOne, form.Find("li").TextContent));
    }

    // Mutations that must break it: report only when the edit returned false (the field is never
    // marked modified), and return the negation of what the edit returned (the Assert.True fails).
    [Fact]
    public async Task An_async_TryEdit_returning_true_reports_and_returns_true()
    {
        var only = new RowListItem();
        var model = new RowListModel { Items = [only] };
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);

        var result = await form.InvokeAsync(() => context.Value!.TryEdit(async () =>
        {
            await Task.Yield();
            return model.Items.Remove(only);
        }));

        Assert.True(result);
        Assert.True(form.Instance.Engine!.EditContext.IsModified(ItemsField(model)));
        form.WaitForAssertion(() => Assert.Equal(AtLeastOne, form.Find("li").TextContent));
    }

    // Mutation that must break it: report whatever the edit returned, and the field is marked
    // modified.
    [Fact]
    public async Task An_async_TryEdit_returning_false_reports_nothing()
    {
        var model = new RowListModel();
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);

        var result = await form.InvokeAsync(() => context.Value!.TryEdit(async () =>
        {
            await Task.Yield();
            return model.Items.Remove(new RowListItem());
        }));

        Assert.False(result);
        Assert.False(form.Instance.Engine!.EditContext.IsModified(ItemsField(model)));
        Assert.Empty(form.FindAll("li"));
    }

    // Both awaiting methods, Edit and TryEdit, each with an edit that throws before returning a
    // task, one that faults after an await, and one that is cancelled. Mutation that must break
    // it: report in a finally, and the field is marked modified on the way out.
    [Fact]
    public async Task An_async_edit_that_faults_or_is_cancelled_reports_nothing()
    {
        var model = new RowListModel { Items = [new RowListItem()] };
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);
        var cancelledToken = new CancellationToken(canceled: true);
        Func<Task> throwsFirst = () => throw new InvalidOperationException("task, before a task");
        Func<Task> faults = async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("task, after an await");
        };
        Func<Task> cancelled = () => Task.FromCanceled(cancelledToken);
        Func<Task<bool>> throwsFirstBool = () => throw new InvalidOperationException("bool, before a task");
        Func<Task<bool>> faultsBool = async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("bool, after an await");
        };
        Func<Task<bool>> cancelledBool = () => Task.FromCanceled<bool>(cancelledToken);

        foreach (var (edit, message) in new[]
        {
            (throwsFirst, "task, before a task"),
            (faults, "task, after an await"),
        })
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => form.InvokeAsync(() => context.Value!.Edit(edit)));
            Assert.Equal(message, thrown.Message);
        }

        foreach (var (edit, message) in new[]
        {
            (throwsFirstBool, "bool, before a task"),
            (faultsBool, "bool, after an await"),
        })
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => form.InvokeAsync(() => context.Value!.TryEdit(edit)));
            Assert.Equal(message, thrown.Message);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => form.InvokeAsync(() => context.Value!.Edit(cancelled)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => form.InvokeAsync(() => context.Value!.TryEdit(cancelledBool)));

        Assert.Single(model.Items);
        Assert.False(form.Instance.Engine!.EditContext.IsModified(ItemsField(model)));
    }

    // Each call throws before it returns, the awaiting Edit and TryEdit included: a faulted task in
    // their place would reach Assert.Throws as no exception at all. Mutation that must break it:
    // drop the null test from any one of the four methods (four runs), and that call throws
    // NullReferenceException or returns a faulted task instead.
    [Fact]
    public void Edit_rejects_a_null_delegate()
    {
        var model = new RowListModel();
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);
        var field = context.Value!;

        var action = Assert.Throws<ArgumentNullException>(() => field.Edit((Action)null!));
        var func = Assert.Throws<ArgumentNullException>(() => { _ = field.TryEdit((Func<bool>)null!); });
        var task = Assert.Throws<ArgumentNullException>(() => { _ = field.Edit((Func<Task>)null!); });
        var taskBool = Assert.Throws<ArgumentNullException>(() => { _ = field.TryEdit((Func<Task<bool>>)null!); });

        Assert.All([action, func, task, taskBool], thrown => Assert.Equal("edit", thrown.ParamName));
        Assert.False(form.Instance.Engine!.EditContext.IsModified(ItemsField(model)));
    }

    // The fixture is Razor-compiled, so the overload each markup lambda binds is the one the
    // Razor compiler's generated C# picks. Each button gets a fresh form, because a field stays
    // modified once it has been. Edit reports whatever its edit returns; TryEdit reports only on
    // true. A ValueTask edit written async () => await binds an awaiting overload, so its report
    // waits for the ValueTask. Mutations that must break it: give Edit a Func<bool> overload
    // again beside Edit(Action) (the absent row's Edit lambda binds it and reports nothing), and
    // delete Edit(Func<Task>) (each async Edit lambda binds Edit(Action) as async void, which
    // reports at the lambda's first await, while the gate is still closed).
    [Fact]
    public async Task Lambdas_in_markup_bind_the_reporting_overloads()
    {
        var edited = RenderHost();
        await edited.Host.Find("#edit-remove-absent").ClickAsync(new());
        Assert.Same(edited.Present, Assert.Single(edited.Model.Items));
        Assert.True(edited.IsModified());

        var absent = RenderHost();
        await absent.Host.Find("#try-remove-absent").ClickAsync(new());
        Assert.Same(absent.Present, Assert.Single(absent.Model.Items));
        Assert.False(absent.IsModified());

        var present = RenderHost();
        await present.Host.Find("#try-remove-present").ClickAsync(new());
        Assert.Empty(present.Model.Items);
        Assert.True(present.IsModified());

        var gated = await ClickBehindTheGateAsync("#add-after-gate");
        Assert.Equal(2, gated.Model.Items.Count);
        Assert.True(gated.IsModified());

        var valueTask = await ClickBehindTheGateAsync("#add-after-value-task");
        Assert.Equal(2, valueTask.Model.Items.Count);
        Assert.True(valueTask.IsModified());

        var valueTaskAbsent = await ClickBehindTheGateAsync("#try-remove-absent-after-value-task");
        Assert.Same(valueTaskAbsent.Present, Assert.Single(valueTaskAbsent.Model.Items));
        Assert.False(valueTaskAbsent.IsModified());

        var valueTaskPresent = await ClickBehindTheGateAsync("#try-remove-present-after-value-task");
        Assert.Empty(valueTaskPresent.Model.Items);
        Assert.True(valueTaskPresent.IsModified());
    }

    private static FieldIdentifier ItemsField(RowListModel model) => new(model, nameof(RowListModel.Items));

    /// <summary>Clicks a button whose edit waits on the host's gate, shows that nothing is reported or edited while the gate is closed, then opens it and awaits the click.</summary>
    private async Task<BindingHost> ClickBehindTheGateAsync(string button)
    {
        var host = RenderHost();
        var click = host.Host.Find(button).ClickAsync(new());
        await host.Host.InvokeAsync(() => { });
        Assert.False(host.IsModified());
        Assert.Same(host.Present, Assert.Single(host.Model.Items));
        Assert.False(click.IsCompleted);

        host.Gate.SetResult();
        await click;
        return host;
    }

    private BindingHost RenderHost(bool listStartsEmpty = false)
    {
        var present = new RowListItem();
        var model = new RowListModel { Items = listStartsEmpty ? [] : [present], Flag = true };
        var gate = new TaskCompletionSource();
        var host = Render<Fixtures.EditBindingHost>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Present, present)
            .Add(p => p.Gate, gate));
        var engine = host.FindComponent<FormidableForm<RowListModel>>().Instance.Engine!;
        return new BindingHost(host, model, present, gate, engine, () => engine.EditContext.IsModified(ItemsField(model)));
    }

    /// <summary>Renders the list the way a page does: a <c>FormidableField</c> wrapping it, and a <c>FormidableCollectionMessage</c> for the list's own rule.</summary>
    private IRenderedComponent<FormidableForm<RowListModel>> RenderList(RowListModel model, StrongBox<FormidableFieldContext> context) =>
        this.RenderCollectionField(model, () => model.Items, context);

    private sealed record BindingHost(
        IRenderedComponent<Fixtures.EditBindingHost> Host,
        RowListModel Model,
        RowListItem Present,
        TaskCompletionSource Gate,
        IFormidableEngine Engine,
        Func<bool> IsModified);
}
