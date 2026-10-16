using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>Turns one engine publish into one render batch: it renders nothing, and raising <see cref="IFormidableEngine.StateChanged"/> from its render puts every subscriber's re-render into the batch that render opened.</summary>
// Without the relay, a publish raised outside a render batch (a check ending, a debounce firing,
// a server reply applied after an await) costs one batch per subscriber: each handler's
// StateHasChanged runs inline and the renderer, with no batch in progress, processes it at once.
// StateHasChanged on this component is the one way a library can open a batch; raising the event
// inside the render it opens is the pattern CascadingValue uses to re-supply its subscribers.
// Inside an event dispatch a batch is already in progress, so the relay's render cannot run yet;
// the engine then raises directly, and Blazor coalesces the subscribers' renders into the running
// batch. Never override ShouldRender here: the render is the delivery, so a render skipped is a
// publish lost (the batch pin would catch it).
internal sealed class StateChangedRelay : ComponentBase, IDisposable
{
    private readonly Queue<FormidableStateChangedEventArgs> _pending = new();
    private IStateChangedPublisher? _attached;
    private int _renders;
    private int _publishDepth;
    private ExceptionDispatchInfo? _captured;

    /// <summary>The engine whose publishes this relay delivers.</summary>
    [Parameter]
    public IStateChangedPublisher Publisher { get; set; } = default!;

    /// <summary>Attaches to <see cref="Publisher"/>, moving off any previous one.</summary>
    protected override void OnParametersSet()
    {
        if (ReferenceEquals(_attached, Publisher))
        {
            return;
        }

        _attached?.DetachRelay(this);
        _attached = Publisher;
        _attached.AttachRelay(this);
    }

    /// <summary>Delivers one publish: through this relay's render when no batch is in progress, else directly; a handler's throw leaves this call either way.</summary>
    /// <param name="args">The publish's arguments.</param>
    internal void Publish(FormidableStateChangedEventArgs args)
    {
        _pending.Enqueue(args);
        var rendersBefore = _renders;
        // A depth, not a flag: a handler that publishes again from inside this relay's render
        // nests a Publish in this one, and its return must not read as the outer publish ending.
        _publishDepth++;
        try
        {
            StateHasChanged();
        }
        catch
        {
            // The renderer refused the call (made off its dispatcher), so nothing raised the
            // queued publish. Left in the queue it would ride the next publish, late and out of
            // order; the throw reaches the caller, as it does from Blazor's own form components.
            _pending.Clear();
            throw;
        }
        finally
        {
            _publishDepth--;
        }

        if (_renders != rendersBefore)
        {
            // The renderer ran this component's render synchronously: no batch was in progress,
            // the render raised the queued publish, and every subscriber's render joined the
            // batch it opened. A handler's throw was kept for here, so the call that published
            // still sees it.
            var captured = _captured;
            _captured = null;
            captured?.Throw();
            return;
        }

        // A batch is in progress (an event dispatch, or a render of this relay already queued in
        // it), so the render above cannot run yet. Raising directly here is what an engine with
        // no relay does; the subscribers' renders coalesce into the running batch.
        while (_pending.TryDequeue(out var queued))
        {
            Raise(queued);
        }
    }

    /// <summary>Raises each queued publish; renders nothing.</summary>
    /// <param name="builder">The render tree builder, left empty.</param>
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        _renders++;
        while (_pending.TryDequeue(out var args))
        {
            try
            {
                Raise(args);
            }
            catch (Exception exception)
            {
                // Kept, not thrown: a throw out of a render would reach the boundary wrapped and
                // leave the batch half done. Publish rethrows it once the batch has completed.
                _captured ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }

    /// <summary>Hands the renderer a kept throw that no <see cref="Publish"/> is waiting to rethrow, which the delivery rules say cannot happen; defensive only.</summary>
    /// <param name="firstRender">Whether this is the first render; not consulted.</param>
    protected override void OnAfterRender(bool firstRender)
    {
        if (_publishDepth > 0 || _captured is null)
        {
            return;
        }

        var captured = _captured;
        _captured = null;
        _ = DispatchExceptionAsync(captured.SourceException);
    }

    /// <summary>Detaches from the engine.</summary>
    public void Dispose()
    {
        _attached?.DetachRelay(this);
        _attached = null;
    }

    /// <summary>Raises one publish through the engine this relay is attached to.</summary>
    /// <param name="args">The publish's arguments.</param>
    // Through the attachment rather than the parameter, as attach and detach are: the engine that
    // calls Publish is the one AttachRelay bound, and a parameter set that swaps Publisher moves
    // the attachment before any render or publish can run against the new one.
    private void Raise(FormidableStateChangedEventArgs args) => _attached!.RaiseStateChanged(args);
}
