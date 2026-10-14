using Bunit;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Blazor's own <see cref="InputText"/> reports a change without waiting for an async setter to
/// finish, so the check that report starts reads what the setter wrote before its first await. A
/// setter that writes the model first, and a <c>@bind-Value</c> binding that moves the async work
/// to <c>@bind-Value:after</c>, each leave no message for the value the edit replaced.
/// </summary>
/// <remarks>
/// The clock is a <see cref="FakeTimeProvider"/>, so the whole-form re-check after the first render
/// runs only when a test moves the clock, and the edit's own check is the one that answers.
/// </remarks>
public class NativeInputAsyncSetterTests : BunitContext
{
    private readonly FakeTimeProvider _clock = new();

    public NativeInputAsyncSetterTests()
    {
        Services.AddFormidable();
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddSingleton<FluentValidation.IValidator<EngineOrder>, EngineOrderValidator>();
    }

    // Mutation that must break it: move the setter's write after its await, and the check reads
    // the empty description, so its required message shows beside "Bob".
    [Fact]
    public Task A_setter_that_writes_before_its_first_await_leaves_no_stale_message() =>
        AssertNoStaleMessageAsync(bindAfter: false);

    // Mutation that must break it: bind with @bind-Value:set to a setter that awaits the same work
    // before it writes, and the empty description's required message shows beside "Bob".
    [Fact]
    public Task Async_work_moved_to_bind_value_after_leaves_no_stale_message() =>
        AssertNoStaleMessageAsync(bindAfter: true);

    private async Task AssertNoStaleMessageAsync(bool bindAfter)
    {
        var order = new EngineOrder();
        var cut = Render<AsyncSetterHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.BindAfter, bindAfter));
        var engine = cut.FindComponent<FormidableForm<EngineOrder>>().Instance.Engine!;

        await cut.InvokeAsync(() => cut.Find("input").Change("Bob"));
        cut.WaitForState(() => order.Description == "Bob" && !engine.IsValidating);
        await cut.InvokeAsync(() => { });

        Assert.Empty(engine.GetIssues(FieldIdentifier.Create(() => order.Description)));
        Assert.Empty(cut.FindAll(".validation-message"));
    }
}
