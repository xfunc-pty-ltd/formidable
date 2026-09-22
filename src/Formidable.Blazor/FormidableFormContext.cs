using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>What a root cascades to the components inside it: the engine, its <see cref="EditContext"/> and <see cref="Registry"/>, and the root's own first-error focus move.</summary>
public sealed class FormidableFormContext
{
    private readonly Func<Task<bool>>? _focusFirstError;

    /// <summary>Wraps an engine for a test that cascades a context by hand; the two roots build their own.</summary>
    /// <param name="engine">The engine the context exposes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A context built this way has no root behind it, so <see cref="FocusFirstErrorAsync"/>
    /// moves nothing and answers <see langword="false"/>, and nothing tells its engine when a
    /// field leaves the page.
    /// </remarks>
    public FormidableFormContext(IFormidableEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Engine = engine;
    }

    /// <summary>The roots' constructor: the engine plus the root's own <c>FocusFirstErrorAsync</c>.</summary>
    /// <param name="engine">The engine the context exposes.</param>
    /// <param name="focusFirstError">The root's <c>FocusFirstErrorAsync</c>, so its <c>PrepareFocus</c> and <c>FocusFallback</c> travel with the move.</param>
    // A delegate over the root's public method rather than over the move itself, deliberately:
    // the move has exactly one implementation, and it is reached through the root so that the
    // root's PrepareFocus and FocusFallback come with it.
    internal FormidableFormContext(IFormidableEngine engine, Func<Task<bool>> focusFirstError)
        : this(engine) => _focusFirstError = focusFirstError;

    /// <summary>The form's validation engine.</summary>
    public IFormidableEngine Engine { get; }

    /// <summary>The form's <see cref="Microsoft.AspNetCore.Components.Forms.EditContext"/>.</summary>
    public EditContext EditContext => Engine.EditContext;

    /// <summary>The registry the components inside the form register with as they render.</summary>
    public FieldRegistry Registry => Engine.Registry;

    /// <summary>Moves focus to the first visible error through the root that cascaded this context, and reports whether an element took it.</summary>
    /// <returns><see langword="true"/> when an element took focus; <see langword="false"/> when nothing moved, or when no root is behind this context.</returns>
    /// <remarks>Call it from the renderer's synchronization context.</remarks>
    // The one member here that is not the engine's, because the engine cannot make this move:
    // the parameters governing it are the root's, and no focus service is reachable from the
    // engine at all. ResetAsync shows where the line falls: returning a form to pristine
    // rebuilds the engine, so a context asked to do it would invalidate the very instance it was
    // asked on, where moving focus invalidates nothing. That one stays on the component.
    public Task<bool> FocusFirstErrorAsync() =>
        _focusFirstError?.Invoke() ?? Task.FromResult(false);
}
