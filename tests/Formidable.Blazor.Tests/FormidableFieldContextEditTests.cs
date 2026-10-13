using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins <c>Edit</c> and <c>TryEdit</c> on <c>FormidableFieldContext</c>: each runs the page's own
/// edit, then reports the change for the field, so the check it starts reads the edited list.
/// <c>Edit</c> always reports; a <c>TryEdit</c> whose edit returns <see langword="false"/> reports
/// nothing, and so does an edit that throws, faults or is cancelled. An awaited edit reports to the
/// engine its root holds once the edit completes, when that engine edits the same model.
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
    // Edit a Func<bool> overload beside Edit(Action), and the assignment of false binds it and
    // reports nothing.
    [Fact]
    public async Task Edit_with_a_bool_assignment_always_reports()
    {
        var host = RenderHost(listStartsEmpty: true);

        await host.Host.InvokeAsync(() => host.Host.Find("#assign-false").ClickAsync(new()));

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

    // Without Edit(Func<ValueTask>), this shape binds Edit(Action) and reports before the edit
    // completes. The helper takes the call's task, so a binding to Edit(Action), which returns
    // nothing, does not compile. Mutation that must break it: report before awaiting the edit in
    // Edit(Func<ValueTask>), and the field is reported while the gate is still closed.
    [Fact]
    public Task An_expression_lambda_returning_ValueTask_waits_for_the_edit() =>
        AssertTheReportWaitsForTheEditAsync((field, rows) => field.Edit(() => rows.ClearAsync()));

    // Neither older overload takes this method group: Edit(Action) needs a void method and
    // Edit(Func<Task>) a Task.
    [Fact]
    public Task A_method_group_returning_ValueTask_binds_the_ValueTask_overload() =>
        AssertTheReportWaitsForTheEditAsync((field, rows) => field.Edit(rows.ClearAsync));

    // A block lambda that returns a value cannot become an Action.
    [Fact]
    public Task A_block_lambda_returning_ValueTask_binds_it() =>
        AssertTheReportWaitsForTheEditAsync((field, rows) => field.Edit(() => { return rows.ClearAsync(); }));

    [Fact]
    public Task An_explicit_ValueTask_return_type_binds_it() =>
        AssertTheReportWaitsForTheEditAsync((field, rows) => field.Edit(ValueTask () => rows.ClearAsync()));

    // One form, the false edit first, because a field stays modified once it has been. Mutations
    // that must break it: report whatever the edit returned (the field is reported for the absent
    // row), and never report (the present row's edit is not heard).
    [Fact]
    public async Task TryEdit_with_a_ValueTask_of_bool_cancels_on_false()
    {
        var present = new RowListItem();
        var model = new RowListModel { Items = [present] };
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);
        var engine = form.Instance.Engine!;
        var heard = HearFieldChanges(engine.EditContext);
        var absentRows = new GatedRows(model.Items);
        var presentRows = new GatedRows(model.Items);
        var absent = Task.FromResult(true);
        var removed = Task.FromResult(false);

        await form.InvokeAsync(() => { absent = context.Value!.TryEdit(() => absentRows.RemoveAsync(new RowListItem())); });
        Assert.False(absent.IsCompleted);
        absentRows.Gate.SetResult();

        Assert.False(await absent);
        Assert.Same(present, Assert.Single(model.Items));
        Assert.Empty(heard);
        Assert.False(engine.EditContext.IsModified(ItemsField(model)));

        await form.InvokeAsync(() => { removed = context.Value!.TryEdit(() => presentRows.RemoveAsync(present)); });
        Assert.False(removed.IsCompleted);
        Assert.Empty(heard);
        presentRows.Gate.SetResult();

        Assert.True(await removed);
        Assert.Empty(model.Items);
        Assert.Equal([ItemsField(model)], heard);
        Assert.True(engine.EditContext.IsModified(ItemsField(model)));
    }

    // An async lambda converts to Func<Task> and Func<ValueTask> alike, and the priority on the
    // Task overloads settles the call, so the compile is this test's pin. Mutation that must break
    // it: remove the priority, and both calls are ambiguous (CS0121). Nothing at run time tells the
    // two overloads apart: each returns a task, awaits the edit and then reports, so the checks
    // below hold whichever one an async lambda binds.
    [Fact]
    public async Task An_async_lambda_still_binds_the_Task_overload()
    {
        var present = new RowListItem();
        var model = new RowListModel { Items = [present] };
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);
        var engine = form.Instance.Engine!;
        var removeRows = new GatedRows(model.Items);
        var clearRows = new GatedRows(model.Items);
        var tryEdit = Task.FromResult(false);
        var edit = Task.CompletedTask;

        await form.InvokeAsync(() =>
        {
            tryEdit = context.Value!.TryEdit(async () => await removeRows.RemoveAsync(present));
            edit = context.Value!.Edit(async () => await clearRows.ClearAsync());
        });
        Assert.False(tryEdit.IsCompleted);
        Assert.False(edit.IsCompleted);
        Assert.False(engine.EditContext.IsModified(ItemsField(model)));

        removeRows.Gate.SetResult();
        Assert.True(await tryEdit);
        clearRows.Gate.SetResult();
        await edit;
        Assert.Empty(model.Items);
        Assert.True(engine.EditContext.IsModified(ItemsField(model)));
    }

    // Both ValueTask methods, each with an edit that throws before returning a ValueTask, one that
    // faults after an await, and one that is cancelled: the fault reaches the caller through the
    // returned task. Mutation that must break it: report in a finally, and the field is marked
    // modified on the way out.
    [Fact]
    public async Task A_ValueTask_edit_that_faults_or_is_cancelled_reports_nothing()
    {
        var model = new RowListModel { Items = [new RowListItem()] };
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);
        var cancelledToken = new CancellationToken(canceled: true);
        Func<ValueTask> throwsFirst = () => throw new InvalidOperationException("value task, before a task");
        Func<ValueTask> faults = async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("value task, after an await");
        };
        Func<ValueTask> cancelled = () => ValueTask.FromCanceled(cancelledToken);
        Func<ValueTask<bool>> throwsFirstBool = () => throw new InvalidOperationException("value bool, before a task");
        Func<ValueTask<bool>> faultsBool = async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("value bool, after an await");
        };
        Func<ValueTask<bool>> cancelledBool = () => ValueTask.FromCanceled<bool>(cancelledToken);

        foreach (var (edit, message) in new[]
        {
            (throwsFirst, "value task, before a task"),
            (faults, "value task, after an await"),
        })
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => form.InvokeAsync(() => context.Value!.Edit(edit)));
            Assert.Equal(message, thrown.Message);
        }

        foreach (var (edit, message) in new[]
        {
            (throwsFirstBool, "value bool, before a task"),
            (faultsBool, "value bool, after an await"),
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
    // drop the null test from any one of the six methods (six runs), and that call throws
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
        var valueTask = Assert.Throws<ArgumentNullException>(() => { _ = field.Edit((Func<ValueTask>)null!); });
        var valueTaskBool = Assert.Throws<ArgumentNullException>(() => { _ = field.TryEdit((Func<ValueTask<bool>>)null!); });

        Assert.All([action, func, task, taskBool, valueTask, valueTaskBool], thrown => Assert.Equal("edit", thrown.ParamName));
        Assert.False(form.Instance.Engine!.EditContext.IsModified(ItemsField(model)));
    }

    // The fixture is Razor-compiled, so the overload each markup lambda binds is the one the
    // Razor compiler's generated C# picks. Each button gets a fresh form, because a field stays
    // modified once it has been. Edit reports whatever its edit returns; TryEdit reports only on
    // true. A ValueTask edit written async () => await binds a Task overload; one handed straight
    // to Edit or TryEdit binds a ValueTask overload, whose task the handler returns. Either way
    // the report and the click wait for the ValueTask. Mutations that must break it: give Edit a
    // Func<bool> overload beside Edit(Action) (the absent row's Edit lambda binds it and reports
    // nothing), remove the priority from the Task overloads (the async lambda rows stop
    // compiling, CS0121), report before awaiting in the ValueTask overloads (the straight rows
    // report while the gate is still closed), and return a ValueTask from the ValueTask overloads
    // (Blazor drops it, and the straight rows' clicks complete while the gate is still closed).
    [Fact]
    public async Task Lambdas_in_markup_bind_the_reporting_overloads()
    {
        var edited = RenderHost();
        await edited.Host.InvokeAsync(() => edited.Host.Find("#edit-remove-absent").ClickAsync(new()));
        Assert.Same(edited.Present, Assert.Single(edited.Model.Items));
        Assert.True(edited.IsModified());

        var absent = RenderHost();
        await absent.Host.InvokeAsync(() => absent.Host.Find("#try-remove-absent").ClickAsync(new()));
        Assert.Same(absent.Present, Assert.Single(absent.Model.Items));
        Assert.False(absent.IsModified());

        var present = RenderHost();
        await present.Host.InvokeAsync(() => present.Host.Find("#try-remove-present").ClickAsync(new()));
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

        var straight = await ClickBehindTheGateAsync("#add-value-task");
        Assert.Equal(2, straight.Model.Items.Count);
        Assert.True(straight.IsModified());

        var straightPresent = await ClickBehindTheGateAsync("#try-remove-present-value-task");
        Assert.Empty(straightPresent.Model.Items);
        Assert.True(straightPresent.IsModified());
    }

    // A handler that returns Edit's task hands Blazor something to await, so the click waits for
    // the edit, and the edit's exception reaches the renderer, where Blazor Server ends the circuit
    // and WebAssembly shows its error UI. Mutation that must break it: return a ValueTask from
    // Edit(Func<ValueTask>), and Blazor drops it: the click completes while the gate is still
    // closed, and the renderer never hears of the exception.
    [Fact]
    public async Task A_ValueTask_edit_in_markup_hands_its_exception_to_the_renderer()
    {
        var failed = await ClickBehindTheGateAsync("#fail-value-task");

        var thrown = await Renderer.UnhandledException.WaitAsync(TimeSpan.Zero);
        Assert.Equal(Fixtures.EditBindingHost.FailMessage, Assert.IsType<InvalidOperationException>(thrown).Message);
        Assert.Same(failed.Present, Assert.Single(failed.Model.Items));
        Assert.False(failed.IsModified());
    }

    // A reset over the same model rebuilds the form's engine while the edit awaits. The field
    // names an object the new engine still edits, so the report follows the form there. Mutation
    // that must break it: report to the engine the context was built with, and the new engine
    // hears nothing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_awaiting_edit_reports_to_the_engine_a_reset_built_over_the_same_model(bool tryEdit)
    {
        var model = new RowListModel { Items = [new RowListItem()] };
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);
        var built = form.Instance.Engine!;
        var gate = new TaskCompletionSource();
        var edit = await StartGatedEditAsync(form, context.Value!, gate, tryEdit, () => model.Items.Clear());

        await form.InvokeAsync(() => form.Instance.ResetAsync());
        var current = form.Instance.Engine!;
        Assert.NotSame(built, current);
        var heard = HearFieldChanges(current.EditContext);

        gate.SetResult();
        await edit;

        Assert.Equal([ItemsField(model)], heard);
        Assert.True(current.EditContext.IsModified(ItemsField(model)));
        form.WaitForAssertion(() => Assert.Equal(AtLeastOne, form.Find("li").TextContent));
    }

    // The ValueTask methods follow the root as the Task ones do. Mutation that must break it:
    // report from the ValueTask methods as NotifyChanged does, to the engine the context was built
    // with, and the new engine hears nothing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_ValueTask_edit_reports_to_the_engine_a_reset_built_over_the_same_model(bool tryEdit)
    {
        var present = new RowListItem();
        var model = new RowListModel { Items = [present] };
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);
        var built = form.Instance.Engine!;
        var rows = new GatedRows(model.Items);
        var edit = Task.CompletedTask;

        await form.InvokeAsync(() =>
        {
            edit = tryEdit
                ? context.Value!.TryEdit(() => rows.RemoveAsync(present))
                : context.Value!.Edit(() => rows.ClearAsync());
        });
        Assert.False(edit.IsCompleted);

        await form.InvokeAsync(() => form.Instance.ResetAsync());
        var current = form.Instance.Engine!;
        Assert.NotSame(built, current);
        var heard = HearFieldChanges(current.EditContext);
        var heardBuilt = HearFieldChanges(built.EditContext);

        rows.Gate.SetResult();
        await edit;

        Assert.Empty(model.Items);
        Assert.Equal([ItemsField(model)], heard);
        Assert.Empty(heardBuilt);
        Assert.True(current.EditContext.IsModified(ItemsField(model)));
    }

    // A reset to another model under @bind-Model: the new engine never held a field on the old
    // model, so the report goes nowhere, the old engine's EditContext included. Mutation that must
    // break it: drop the same-model test, and the new engine hears a field on the old model.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_awaiting_edit_reports_to_nobody_after_a_reset_to_another_model(bool tryEdit)
    {
        var oldModel = new RowListModel { Items = [new RowListItem()] };
        var newModel = new RowListModel { Items = [new RowListItem()] };
        var context = new StrongBox<FormidableFieldContext>();
        var host = Render<BoundModelHost>(parameters => parameters
            .Add(p => p.InitialModel, oldModel)
            .Add(p => p.Captured, context));
        var form = host.FindComponent<FormidableForm<RowListModel>>();
        var built = form.Instance.Engine!;
        var gate = new TaskCompletionSource();
        var edit = await StartGatedEditAsync(form, context.Value!, gate, tryEdit, () => oldModel.Items.Clear());

        await form.InvokeAsync(() => form.Instance.ResetAsync(newModel));
        var current = form.Instance.Engine!;
        Assert.Same(newModel, current.EditContext.Model);
        var heard = HearFieldChanges(current.EditContext);

        gate.SetResult();
        await edit;

        Assert.Empty(heard);
        Assert.False(built.EditContext.IsModified(ItemsField(oldModel)));
    }

    // Mutation that must break it: report to the engine the context was built with whatever the
    // form holds, and the disposed form's EditContext marks the field modified.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_awaiting_edit_reports_to_nobody_once_the_form_is_disposed(bool tryEdit)
    {
        var model = new RowListModel { Items = [new RowListItem()] };
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);
        var built = form.Instance.Engine!;
        var gate = new TaskCompletionSource();
        var edit = await StartGatedEditAsync(form, context.Value!, gate, tryEdit, () => model.Items.Clear());
        var heard = HearFieldChanges(built.EditContext);

        await DisposeComponentsAsync();
        gate.SetResult();
        await edit;

        Assert.Empty(heard);
        Assert.False(built.EditContext.IsModified(ItemsField(model)));
    }

    // FormidableValidator survives an EditContext its host cascades by hand and swaps, and builds a
    // new engine over the same model, so the report follows it there. Mutation that must break
    // it: report to the engine the context was built with, and the new engine hears nothing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_awaiting_edit_under_FormidableValidator_follows_a_hand_cascaded_EditContext_over_the_same_model(bool tryEdit)
    {
        var model = new RowListModel { Items = [new RowListItem()] };
        var context = new StrongBox<FormidableFieldContext>();
        var host = Render<HandCascadedHost>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Captured, context));
        var validator = host.FindComponent<FormidableValidator<RowListModel>>();
        var built = validator.Instance.Engine!;
        var gate = new TaskCompletionSource();
        var edit = await StartGatedEditAsync(host, context.Value!, gate, tryEdit, () => model.Items.Clear());

        await host.InvokeAsync(host.Instance.ReplaceEditContext);
        Assert.Same(validator.Instance, host.FindComponent<FormidableValidator<RowListModel>>().Instance);
        var current = validator.Instance.Engine!;
        Assert.NotSame(built, current);
        Assert.Same(host.Instance.EditContext, current.EditContext);
        var heard = HearFieldChanges(current.EditContext);

        gate.SetResult();
        await edit;

        Assert.Equal([ItemsField(model)], heard);
        Assert.True(current.EditContext.IsModified(ItemsField(model)));
    }

    // An EditForm keys its content on its EditContext, so replacing the EditContext disposes the
    // FormidableValidator inside it and builds another. The disposed one holds no engine, and the
    // new one is a different root, so the report goes nowhere. Mutation that must break it: fall
    // back to the engine the context was built with when the root holds none, and the old
    // EditContext marks the field modified.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_awaiting_edit_under_an_EditForm_whose_EditContext_is_replaced_reports_to_nobody(bool tryEdit)
    {
        var model = new RowListModel { Items = [new RowListItem()] };
        var context = new StrongBox<FormidableFieldContext>();
        var host = Render<EditFormHost>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Captured, context));
        var first = host.FindComponent<FormidableValidator<RowListModel>>().Instance;
        var oldEditContext = host.Instance.EditContext;
        var gate = new TaskCompletionSource();
        var edit = await StartGatedEditAsync(host, context.Value!, gate, tryEdit, () => model.Items.Clear());
        var heardOld = HearFieldChanges(oldEditContext);

        await host.InvokeAsync(host.Instance.ReplaceEditContext);
        var second = host.FindComponent<FormidableValidator<RowListModel>>().Instance;
        Assert.NotSame(first, second);
        Assert.Null(first.Engine);
        var heardNew = HearFieldChanges(second.Engine!.EditContext);

        gate.SetResult();
        await edit;

        Assert.Empty(heardOld);
        Assert.Empty(heardNew);
        Assert.False(oldEditContext.IsModified(ItemsField(model)));
        Assert.False(second.Engine!.EditContext.IsModified(ItemsField(model)));
    }

    // A pin: a context built through FormidableFormContext's public constructor has no root behind
    // it, so an awaited edit reports to the engine that context wraps. Mutation that must break it:
    // answer null from ReadCurrentEngine when no root handed in a reader, and the engine hears
    // nothing.
    [Fact]
    public async Task An_awaiting_edit_under_a_hand_built_form_context_reports_to_its_engine()
    {
        var model = new RowListModel { Items = [new RowListItem()] };
        var form = RenderList(model, new StrongBox<FormidableFieldContext>());
        var engine = form.Instance.Engine!;
        var context = new StrongBox<FormidableFieldContext>();
        var cut = Render(builder =>
        {
            builder.OpenComponent<CascadingValue<FormidableFormContext>>(0);
            builder.AddComponentParameter(1, "Value", new FormidableFormContext(engine));
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner => RenderItemsField(inner, () => model.Items, context)));
            builder.CloseComponent();
        });
        var heard = HearFieldChanges(engine.EditContext);
        var gate = new TaskCompletionSource();
        var edit = await StartGatedEditAsync(cut, context.Value!, gate, tryEdit: false, () => model.Items.Clear());

        gate.SetResult();
        await edit;

        Assert.Equal([ItemsField(model)], heard);
        Assert.True(engine.EditContext.IsModified(ItemsField(model)));
    }

    private static FieldIdentifier ItemsField(RowListModel model) => new(model, nameof(RowListModel.Items));

    /// <summary>Clicks a button whose edit waits on the host's gate, shows that nothing is reported or edited while the gate is closed, then opens it and awaits the click.</summary>
    private async Task<BindingHost> ClickBehindTheGateAsync(string button)
    {
        var host = RenderHost();
        var click = host.Host.InvokeAsync(() => host.Host.Find(button).ClickAsync(new()));
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

    /// <summary>Starts an awaiting <c>Edit</c> or <c>TryEdit</c> (the latter returning <see langword="true"/>) whose change waits on <paramref name="gate"/>, from the renderer's context, and hands back its task.</summary>
    private static async Task<Task> StartGatedEditAsync<TComponent>(
        IRenderedComponent<TComponent> cut,
        FormidableFieldContext field,
        TaskCompletionSource gate,
        bool tryEdit,
        Action change)
        where TComponent : IComponent
    {
        var edit = Task.CompletedTask;
        await cut.InvokeAsync(() =>
        {
            edit = tryEdit
                ? field.TryEdit(async () =>
                {
                    await gate.Task;
                    change();
                    return true;
                })
                : field.Edit(async () =>
                {
                    await gate.Task;
                    change();
                });
        });
        Assert.False(edit.IsCompleted);
        return edit;
    }

    /// <summary>Starts <paramref name="start"/>'s edit of a one-row list from the renderer's context, shows that nothing is edited or reported while the edit's gate is closed, then opens it and shows the change reported once, after the edit.</summary>
    private async Task AssertTheReportWaitsForTheEditAsync(Func<FormidableFieldContext, GatedRows, Task> start)
    {
        var model = new RowListModel { Items = [new RowListItem()] };
        var context = new StrongBox<FormidableFieldContext>();
        var form = RenderList(model, context);
        var engine = form.Instance.Engine!;
        var heard = HearFieldChanges(engine.EditContext);
        var rows = new GatedRows(model.Items);
        var edit = Task.CompletedTask;

        await form.InvokeAsync(() => { edit = start(context.Value!, rows); });

        Assert.False(edit.IsCompleted);
        Assert.Single(model.Items);
        Assert.Empty(heard);
        Assert.False(engine.EditContext.IsModified(ItemsField(model)));

        rows.Gate.SetResult();
        await edit;

        Assert.Empty(model.Items);
        Assert.Equal([ItemsField(model)], heard);
        Assert.True(engine.EditContext.IsModified(ItemsField(model)));
    }

    /// <summary>Repository-shaped edits of a list that each return a <see cref="ValueTask"/> and touch the list only once <see cref="Gate"/> opens.</summary>
    private sealed class GatedRows(IList<RowListItem> items)
    {
        public TaskCompletionSource Gate { get; } = new();

        public async ValueTask ClearAsync()
        {
            await Gate.Task;
            items.Clear();
        }

        public async ValueTask<bool> RemoveAsync(RowListItem row)
        {
            await Gate.Task;
            return items.Remove(row);
        }
    }

    /// <summary>Records every field <paramref name="editContext"/> hears change, from this call on.</summary>
    private static List<FieldIdentifier> HearFieldChanges(EditContext editContext)
    {
        var heard = new List<FieldIdentifier>();
        editContext.OnFieldChanged += (_, e) => heard.Add(e.FieldIdentifier);
        return heard;
    }

    /// <summary>A form bound with <c>@bind-Model</c>, so <c>ResetAsync</c> can hand it another model, with a <c>FormidableField</c> over the current model's list.</summary>
    private sealed class BoundModelHost : ComponentBase
    {
        private RowListModel _model = default!;

        [Parameter]
        public RowListModel InitialModel { get; set; } = default!;

        [Parameter]
        public StrongBox<FormidableFieldContext> Captured { get; set; } = default!;

        protected override void OnInitialized() => _model = InitialModel;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<RowListModel>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<RowListModel>.Model), _model);
            builder.AddComponentParameter(
                2,
                nameof(FormidableForm<RowListModel>.ModelChanged),
                EventCallback.Factory.Create<RowListModel>(this, model => _model = model));
            builder.AddComponentParameter(
                3,
                nameof(FormidableForm<RowListModel>.ChildContent),
                (RenderFragment<FormidableFormContext>)(_ => inner => RenderItemsField(inner, () => _model.Items, Captured)));
            builder.CloseComponent();
        }
    }

    /// <summary>
    /// Cascades an <see cref="EditContext"/> the host owns, with a <see cref="FormidableValidator{TModel}"/>
    /// beneath it, and replaces that <see cref="EditContext"/> with a new one over the same model on
    /// <see cref="ReplaceEditContext"/>. Cascading by hand keeps the same validator in place across
    /// the swap, where an <c>EditForm</c> would replace it.
    /// </summary>
    private sealed class HandCascadedHost : ComponentBase
    {
        [Parameter]
        public RowListModel Model { get; set; } = default!;

        [Parameter]
        public StrongBox<FormidableFieldContext> Captured { get; set; } = default!;

        public EditContext EditContext { get; private set; } = default!;

        public void ReplaceEditContext()
        {
            EditContext = new EditContext(Model);
            StateHasChanged();
        }

        protected override void OnInitialized() => EditContext = new EditContext(Model);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<EditContext>>(0);
            builder.AddComponentParameter(1, "Value", EditContext);
            builder.AddComponentParameter(2, "ChildContent", (RenderFragment)(inner => RenderAttachedField(inner, Model, Captured)));
            builder.CloseComponent();
        }
    }

    /// <summary>A real <c>EditForm</c> over a host-owned <see cref="EditContext"/>, with a <see cref="FormidableValidator{TModel}"/> inside, and <see cref="ReplaceEditContext"/> to swap in a new one over the same model.</summary>
    private sealed class EditFormHost : ComponentBase
    {
        [Parameter]
        public RowListModel Model { get; set; } = default!;

        [Parameter]
        public StrongBox<FormidableFieldContext> Captured { get; set; } = default!;

        public EditContext EditContext { get; private set; } = default!;

        public void ReplaceEditContext()
        {
            EditContext = new EditContext(Model);
            StateHasChanged();
        }

        protected override void OnInitialized() => EditContext = new EditContext(Model);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<EditForm>(0);
            builder.AddComponentParameter(1, nameof(EditForm.EditContext), EditContext);
            builder.AddComponentParameter(
                2,
                nameof(EditForm.ChildContent),
                (RenderFragment<EditContext>)(_ => inner => RenderAttachedField(inner, Model, Captured)));
            builder.CloseComponent();
        }
    }

    private static void RenderAttachedField(RenderTreeBuilder builder, RowListModel model, StrongBox<FormidableFieldContext> captured)
    {
        builder.OpenComponent<FormidableValidator<RowListModel>>(0);
        builder.AddComponentParameter(
            1,
            nameof(FormidableValidator<RowListModel>.ChildContent),
            (RenderFragment<FormidableFormContext>)(_ => inner => RenderItemsField(inner, () => model.Items, captured)));
        builder.CloseComponent();
    }

    private static void RenderItemsField(
        RenderTreeBuilder builder,
        Expression<Func<IList<RowListItem>>> accessor,
        StrongBox<FormidableFieldContext> captured)
    {
        builder.OpenComponent<FormidableField<IList<RowListItem>>>(0);
        builder.AddComponentParameter(1, nameof(FormidableField<IList<RowListItem>>.For), accessor);
        builder.AddComponentParameter(
            2,
            nameof(FormidableField<IList<RowListItem>>.ChildContent),
            (RenderFragment<FormidableFieldContext>)(context => _ => captured.Value = context));
        builder.CloseComponent();
    }

    private sealed record BindingHost(
        IRenderedComponent<Fixtures.EditBindingHost> Host,
        RowListModel Model,
        RowListItem Present,
        TaskCompletionSource Gate,
        IFormidableEngine Engine,
        Func<bool> IsModified);
}
