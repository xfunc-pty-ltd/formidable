using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Cascaded to field components: the engine view for one Formidable form, and the one move the
/// root that owns the form makes which the engine cannot.
/// </summary>
public sealed class FormidableFormContext
{
    private readonly Func<Task<bool>>? _focusFirstError;

    /// <summary>Wraps an engine for cascading.</summary>
    /// <remarks>
    /// The two shipped roots — <see cref="FormidableForm{TModel}"/> and
    /// <see cref="FormidableValidator{TModel}"/> — create and cascade this context, and they
    /// are the only supported hosts for one. Constructing it directly is supported for tests
    /// that cascade a context around an engine or a double; a hand-assembled root built this
    /// way never learns of rendered-field-set changes, because the reconciliation and ordering
    /// seams are internal, wired by the shipped hosts. It also carries no root, so
    /// <see cref="FocusFirstErrorAsync"/> has nothing to ask and answers
    /// <see langword="false"/>.
    /// </remarks>
    public FormidableFormContext(IFormidableEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Engine = engine;
    }

    /// <summary>
    /// The shipped roots' constructor: the same engine, plus the root's own first-error move.
    /// </summary>
    /// <param name="engine">The form's validation engine.</param>
    /// <param name="focusFirstError">The root's own <c>FocusFirstErrorAsync</c>. A delegate over
    /// the public method rather than the move itself, deliberately: the move has exactly one
    /// implementation, and it is reached through the root so that the root's
    /// <c>PrepareFocus</c> and <c>FocusFallback</c> come with it.</param>
    internal FormidableFormContext(IFormidableEngine engine, Func<Task<bool>> focusFirstError)
        : this(engine) => _focusFirstError = focusFirstError;

    /// <summary>The form's validation engine.</summary>
    public IFormidableEngine Engine { get; }

    /// <summary>The form's edit context.</summary>
    public EditContext EditContext => Engine.EditContext;

    /// <summary>The disclosure registry field components register with.</summary>
    public FieldRegistry Registry => Engine.Registry;

    /// <summary>
    /// Moves focus to the first error among the form's visible issues — the root's own
    /// <c>FocusFirstErrorAsync</c>, offered to markup and to components inside the form that
    /// have no <c>@ref</c> to reach it with.
    /// </summary>
    /// <remarks>
    /// It is the same move, not a second one. A shared component the page drops inside the form —
    /// the dialog a design system builds once and every form reuses — reads this context, so this
    /// is what lets it complete the dialog sequence (suppress, open, close, focus) without the
    /// page threading its own callback down to it. Which field the visitor lands on, the root's
    /// <c>PrepareFocus</c> being awaited ahead of the attempt, and its <c>FocusFallback</c>
    /// recovering a miss all come with it, because the call goes through the root.
    /// <para>
    /// It is the one thing here that is not the engine's, and the reason is that the engine
    /// cannot make this move: the parameters governing it are the root's, and no focus service is
    /// reachable from the engine at all. <c>ResetAsync</c> is the member that shows where the
    /// line falls — returning a form to pristine REBUILDS the engine, so a context asked to do it
    /// would invalidate the very instance it was asked on, where moving focus invalidates
    /// nothing. That one stays on the component, and a reset button takes an <c>@ref</c>.
    /// </para>
    /// <para>
    /// Call from the renderer's synchronization context (a Blazor event handler or
    /// <c>InvokeAsync</c>). A context built through the public constructor has no root behind it
    /// and answers <see langword="false"/> without attempting anything.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> when an element took focus, and <see langword="false"/> when
    /// nothing did — the form shows no visible issue at all, no
    /// <see cref="IFormidableFocusService"/> is registered, the chosen field's element could not
    /// be focused, or this context was built without a root. It reports the move, not the form,
    /// so a caller that needs to tell those apart reads
    /// <see cref="IFormidableEngine.GetVisibleIssues"/> through <see cref="Engine"/>.
    /// </returns>
    public Task<bool> FocusFirstErrorAsync() =>
        _focusFirstError?.Invoke() ?? Task.FromResult(false);
}
