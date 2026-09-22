using Microsoft.AspNetCore.Components;

namespace Formidable.Blazor;

/// <summary>
/// Registration-only marker for a field rendered by something Formidable does not wrap.
/// Registering keeps automatic disclosure truthful: without it, a submit never reveals the
/// field, so its submit errors are suppressed as unrevealed and its issues sort last in a
/// resolved issue order. The live channel goes by engagement instead and consults registration
/// only under <see cref="LiveIssueDisclosure.EngagedAndVisible"/>, so under the default policy an
/// engaged field's live verdict discloses with or without this marker, and under the opt-in this
/// marker is what lets it. Renders nothing. <see cref="FormidableAccessorComponentBase{TValue}.For"/> is (re-)read
/// whenever the cascaded <see cref="FormidableFormContext"/> is a new instance — including
/// the first render and again after a host such as <c>FormidableForm</c>/<c>FormidableValidator</c>
/// swaps its model and rebuilds its engine and registry — so the registration always targets
/// the currently-active registry.
/// </summary>
/// <typeparam name="TValue">
/// The accessor's type, inferred from <see cref="FormidableAccessorComponentBase{TValue}.For"/>: the field's own value type, or
/// <c>object</c> where a shared component forwards an
/// <c>Expression&lt;Func&lt;object&gt;&gt;</c>.
/// </typeparam>
public sealed class FormidableFieldAnchor<TValue> : FormidableAccessorComponentBase<TValue>
{
    /// <summary>Keeps the field registered after disposal — for virtualized containers.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    /// <summary>
    /// False: an anchor renders nothing, so a validation state change gives it nothing to
    /// re-render — registering the field is its whole job.
    /// </summary>
    protected override bool ObservesEngineState => false;

    /// <inheritdoc />
    protected override FieldRegistration? Register(FormidableFormContext context) =>
        context.Registry.Register(ResolveField(), KeepRegistered);
}
