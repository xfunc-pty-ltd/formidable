using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Registration-only marker for a field rendered by something Formidable does not wrap.
/// Registering keeps automatic disclosure truthful: without it, the field's issues are
/// treated as unrevealed and suppressed. Renders nothing. <see cref="For"/> is (re-)read
/// whenever the cascaded <see cref="FormidableFormContext"/> is a new instance — including
/// the first render and again after a host such as <c>FormidableForm</c>/<c>FormidableValidator</c>
/// swaps its model and rebuilds its engine and registry — so the registration always targets
/// the currently-active registry.
/// </summary>
/// <typeparam name="TValue">The field's value type (inferred from <see cref="For"/>).</typeparam>
public sealed class FormidableFieldAnchor<TValue> : FormidableComponentBase
{
    /// <summary>Accessor for the field to register, e.g. <c>() => Model.Description</c>.</summary>
    [Parameter, EditorRequired]
    public Expression<Func<TValue>> For { get; set; } = default!;

    /// <summary>Keeps the field revealed after disposal — for virtualized containers.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    /// <summary>
    /// False: an anchor renders nothing, so a validation state change gives it nothing to
    /// re-render — registering the field is its whole job.
    /// </summary>
    protected override bool ObservesEngineState => false;

    /// <inheritdoc />
    protected override FieldRegistration? Register(FormidableFormContext context) =>
        context.Registry.Register(
            FieldIdentifier.Create(FieldAccessor.RequireFor(For, GetType())), KeepRegistered);
}
