using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;

// Renderer is the framework's own extension point for hosting components, the one bUnit builds
// on too. BL0006 warns that its types can change between releases, which a test fixture absorbs.
#pragma warning disable BL0006

namespace Formidable.Blazor.Tests.Fixtures;

/// <summary>
/// A renderer shaped like Blazor WebAssembly's: its dispatcher runs every work item at once on the
/// calling thread and installs no synchronization context, so a continuation runs wherever the task
/// it awaited completes. bUnit's renderer runs work on a synchronization context of its own, which
/// posts the continuation of an await that began inside it; this one lets a test watch a
/// continuation run inside the call that completed its task. Call into components with no
/// synchronization context current (an xunit test thread has one), or awaits capture that instead.
/// </summary>
public sealed class InlineRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
{
    /// <summary>What the renderer was handed to report: a lifecycle method's throw, say.</summary>
    public List<Exception> Reported { get; } = [];

    /// <inheritdoc />
    public override Dispatcher Dispatcher { get; } = new InlineDispatcher();

    /// <summary>Instantiates <typeparamref name="TComponent"/> with its services injected, renders it as a root with <paramref name="parameters"/>, and returns it.</summary>
    /// <typeparam name="TComponent">The component type.</typeparam>
    /// <param name="parameters">Its parameters, by name.</param>
    /// <returns>The rendered component.</returns>
    public async Task<TComponent> RenderAsync<TComponent>(IDictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        var component = (TComponent)InstantiateComponent(typeof(TComponent));
        var id = AssignRootComponentId(component);
        await RenderRootComponentAsync(id, ParameterView.FromDictionary(parameters));
        return component;
    }

    /// <inheritdoc />
    protected override void HandleException(Exception exception) => Reported.Add(exception);

    /// <inheritdoc />
    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

    private sealed class InlineDispatcher : Dispatcher
    {
        public override bool CheckAccess() => true;

        public override Task InvokeAsync(Action workItem)
        {
            workItem();
            return Task.CompletedTask;
        }

        public override Task InvokeAsync(Func<Task> workItem) => workItem();

        public override Task<TResult> InvokeAsync<TResult>(Func<TResult> workItem) => Task.FromResult(workItem());

        public override Task<TResult> InvokeAsync<TResult>(Func<Task<TResult>> workItem) => workItem();
    }
}
