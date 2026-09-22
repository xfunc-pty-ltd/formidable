using System.Globalization;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;

namespace Formidable.Blazor;

/// <summary>
/// Base class for validated input components, and the kit's extension point for controls it does
/// not ship: it wires field registration, the engine state subscription, touch/notify plumbing,
/// the computed CSS class, the deterministic element id, and the aria attributes, so a derived
/// control only has to render markup and call <see cref="AddValueBinding"/> to wire its
/// value-commit attribute(s) — the one call that honours <see cref="UpdateOn"/> for every mode,
/// present and future, instead of a derived control re-deciding which DOM event to bind.
/// <see cref="For"/> is (re-)read whenever the cascaded
/// <see cref="FormidableFormContext"/> is a new instance — including the first render and again
/// after a host such as <c>FormidableForm</c>/<c>FormidableValidator</c> swaps its model and
/// rebuilds its engine and registry — so the registration and the engine subscription always
/// target the currently-active context.
/// </summary>
/// <remarks>
/// Two guarantees a derived control inherits and should not work around: a consumer-splatted
/// <c>class</c> is merged with the computed state class rather than replaced (see
/// <see cref="CssClass"/>), and a consumer-supplied <c>id</c> is ignored — the rendered id is
/// always <see cref="ElementId"/>, because message lists, <c>aria-describedby</c> and
/// <see cref="IFormidableFocusService"/> all address the field by it. Render
/// <see cref="AdditionalAttributes"/> first and the computed values after, so the computed values
/// win the duplicate-attribute race; <see cref="FormidableInputText"/> is the reference
/// implementation of that order. The same race catches event handlers, not just attributes:
/// <see cref="AddValueBinding"/>'s own <c>oninput</c>/<c>onchange</c>, and its <c>onblur</c> under
/// <see cref="InputUpdateMode.OnBlur"/>, are rendered after <see cref="AdditionalAttributes"/> as
/// well, so a consumer-splatted <c>@onblur</c> is silently lost under
/// <see cref="InputUpdateMode.OnBlur"/> the same way a consumer-supplied <c>id</c> is.
/// </remarks>
/// <typeparam name="TValue">The field's value type.</typeparam>
public abstract class FormidableInputBase<TValue> : ComponentBase, IDisposable
{
    private static readonly IReadOnlyDictionary<string, object> NoAriaAttributes = new Dictionary<string, object>();

    private readonly FormContextBinding _binding = new();

    /// <summary>
    /// The cascaded form context, and a derived control's route to the engine, the
    /// <c>EditContext</c> and the field registry when the members below are not enough. Supplied by
    /// a <c>FormidableForm</c>/<c>FormidableValidator</c> ancestor: it is null until parameters are
    /// first set, and a control rendered outside such an ancestor throws from
    /// <see cref="OnParametersSet"/> with a message naming the missing ancestor.
    /// </summary>
    [CascadingParameter]
    protected FormidableFormContext? Context { get; private set; }

    /// <summary>Accessor for the bound field, e.g. <c>() => Model.Description</c>.</summary>
    [Parameter, EditorRequired]
    public Expression<Func<TValue>> For { get; set; } = default!;

    /// <summary>The field's current value.</summary>
    [Parameter]
    public TValue? Value { get; set; }

    /// <summary>Raised when the input commits a new value.</summary>
    [Parameter]
    public EventCallback<TValue?> ValueChanged { get; set; }

    /// <summary>Keeps the field revealed after disposal — for virtualized containers.</summary>
    [Parameter]
    public bool KeepRegistered { get; set; }

    /// <summary>Which native DOM event commits the value. Defaults to <see cref="InputUpdateMode.OnChange"/>.</summary>
    [Parameter]
    public InputUpdateMode UpdateOn { get; set; } = InputUpdateMode.OnChange;

    /// <summary>Additional attributes splatted onto the rendered element.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>The resolved field identifier for <see cref="For"/>.</summary>
    protected FieldIdentifier Field { get; private set; }

    /// <summary>
    /// The deterministic element id for this field (see <see cref="FormidableFieldId"/>). Message
    /// lists, <c>aria-describedby</c> and <see cref="IFormidableFocusService"/> all address the
    /// field by this id, so a concrete input must render it as written and must not let a
    /// consumer-splatted <c>id</c> replace it.
    /// </summary>
    protected string ElementId { get; private set; } = string.Empty;

    /// <summary>The field's current state (touched, modified, validating, errors, warnings).</summary>
    protected FieldState State => Context!.Engine.GetFieldState(Field);

    /// <summary>
    /// The CSS class string to render: any <c>class</c> the consumer splatted through
    /// <see cref="AdditionalAttributes"/> first, then the computed state class (see
    /// <see cref="FormidableCss"/>). Merging rather than replacing mirrors the framework's own
    /// <c>InputBase.CssClass</c>, and means a consumer writing <c>class="form-control"</c> keeps
    /// their styling without silently discarding the invalid/valid/pending state class. Render this
    /// <em>after</em> splatting <see cref="AdditionalAttributes"/> so it wins the duplicate-attribute
    /// race (Blazor applies last-write-wins).
    /// </summary>
    /// <remarks>
    /// Returns the merged class string: any consumer-splatted <c>class</c> attribute first,
    /// then the computed state class. Wrapper authors needing the pure state class can call
    /// <see cref="FormidableCss.Compute"/> directly.
    /// </remarks>
    protected string CssClass =>
        CombineClassNames(AdditionalAttributes, FormidableCss.Compute(State, Context!.Engine.Options.CssClasses));

    /// <summary>
    /// Aria attributes to splat onto the element: <c>aria-invalid="true"</c> when the field has
    /// error-severity issues, <c>aria-describedby</c> when it has any issues, an empty dictionary
    /// when clean.
    /// </summary>
    protected IReadOnlyDictionary<string, object> AriaAttributes
    {
        get
        {
            var state = State;
            var issues = Context!.Engine.GetIssues(Field);

            if (!state.HasErrors && issues.Count == 0)
            {
                return NoAriaAttributes;
            }

            var aria = new Dictionary<string, object>();
            if (state.HasErrors)
            {
                aria["aria-invalid"] = "true";
            }

            if (issues.Count > 0)
            {
                aria["aria-describedby"] = $"{ElementId}-messages";
            }

            return aria;
        }
    }

    /// <summary>
    /// Resolves <see cref="For"/>, registers the field, and binds the engine subscription to the
    /// currently-cascaded context. A derived control that overrides this must call
    /// <c>base.OnParametersSet()</c>, or it registers nothing and never re-renders on a
    /// validation state change.
    /// </summary>
    protected override void OnParametersSet() =>
        _binding.Update(
            Context,
            GetType(),
            register: context =>
            {
                Field = FieldIdentifier.Create(For);
                ElementId = FormidableFieldId.For(Field);
                return context.Registry.Register(Field, KeepRegistered);
            },
            stateChanged: OnEngineStateChanged);

    /// <summary>
    /// Assigns <see cref="Value"/>, invokes <see cref="ValueChanged"/>, marks the field touched,
    /// and notifies the EditContext so the engine's live validation pass runs —
    /// <see cref="CommitValueAsync"/> followed by <see cref="NotifyChanged"/>, in one call. Right
    /// for <see cref="InputUpdateMode.OnChange"/>/<see cref="InputUpdateMode.OnInput"/>, where a
    /// single DOM event both commits the value and should start validation; call the two halves
    /// separately instead when an event should do only one (see
    /// <see cref="InputUpdateMode.OnBlur"/>, which <see cref="AddValueBinding"/> implements that
    /// way). A concrete input rendering its own markup can still call this directly from a change
    /// handler for the two combined modes.
    /// </summary>
    protected async Task SetCurrentValueAsync(TValue? value)
    {
        await CommitValueAsync(value);
        NotifyChanged();
    }

    /// <summary>
    /// Assigns <see cref="Value"/> and invokes <see cref="ValueChanged"/> — the value-commit half
    /// of <see cref="SetCurrentValueAsync"/>, without marking the field touched or notifying the
    /// EditContext. Pairs with <see cref="NotifyChanged"/> under
    /// <see cref="InputUpdateMode.OnBlur"/>: the model updates on <c>change</c> even though the
    /// value may not have settled yet (a date input firing per date-segment, for instance), and no
    /// live pass starts until the paired <see cref="NotifyChanged"/> call says it should.
    /// </summary>
    protected Task CommitValueAsync(TValue? value)
    {
        Value = value;
        return ValueChanged.HasDelegate ? ValueChanged.InvokeAsync(value) : Task.CompletedTask;
    }

    /// <summary>
    /// Marks the field touched and notifies the EditContext so the engine's live validation pass
    /// runs — the notify half of <see cref="SetCurrentValueAsync"/>, without touching
    /// <see cref="Value"/>. Pairs with <see cref="CommitValueAsync"/> under
    /// <see cref="InputUpdateMode.OnBlur"/>: call this once the value committed earlier has had a
    /// chance to settle. Named to match <see cref="FormidableFieldContext.NotifyChanged"/>, which
    /// does the same two things for a foreign control with no base class to call it from.
    /// </summary>
    protected void NotifyChanged()
    {
        Context!.Engine.MarkTouched(Field);
        Context.EditContext.NotifyFieldChanged(Field);
    }

    /// <summary>
    /// Adds the attribute(s) that commit a value change, honouring <see cref="UpdateOn"/>: under
    /// <see cref="InputUpdateMode.OnChange"/> (default) or <see cref="InputUpdateMode.OnInput"/> a
    /// single event both commits the value and notifies the engine (see
    /// <see cref="SetCurrentValueAsync"/>); under <see cref="InputUpdateMode.OnBlur"/> the two
    /// split across two events instead — the value commits on <c>change</c> via
    /// <see cref="CommitValueAsync"/>, and the engine is notified separately on <c>blur</c> via
    /// <see cref="NotifyChanged"/>, once the value has had a chance to settle. Reads
    /// <see cref="Value"/> directly rather than taking it as a parameter, since the base already
    /// owns it. Call this last, immediately before <see cref="RenderTreeBuilder.CloseElement"/>:
    /// it consumes <paramref name="sequence"/> and, under <see cref="InputUpdateMode.OnBlur"/>
    /// only, <paramref name="sequence"/> + 1 as well, so nothing else in the render tree should
    /// reuse either number. Also marks <c>value</c> as the attribute the just-added commit handler
    /// updates (<see cref="RenderTreeBuilder.SetUpdatesAttributeName(string)"/>), mirroring native
    /// <c>InputText</c>/<c>InputSelect</c>: before the handler runs, the renderer patches the
    /// current render tree's <c>value</c> frame to match what the browser already holds, so the
    /// following diff emits no edit when nothing actually changed.
    /// </summary>
    protected void AddValueBinding(RenderTreeBuilder builder, int sequence)
    {
        if (UpdateOn == InputUpdateMode.OnBlur)
        {
            builder.AddAttribute(sequence, "onchange", EventCallback.Factory.CreateBinder<TValue?>(this, v => CommitValueAsync(v), Value));
            builder.SetUpdatesAttributeName("value");
            builder.AddAttribute(sequence + 1, "onblur", EventCallback.Factory.Create(this, NotifyChanged));
            return;
        }

        builder.AddAttribute(
            sequence,
            UpdateOn == InputUpdateMode.OnInput ? "oninput" : "onchange",
            EventCallback.Factory.CreateBinder<TValue?>(this, v => SetCurrentValueAsync(v), Value));
        builder.SetUpdatesAttributeName("value");
    }

    /// <summary>
    /// Joins a consumer-splatted <c>class</c> value (first) with the computed state class (last),
    /// tolerating either being absent or empty. Behaviourally equivalent to the framework's
    /// internal splat/class merge, reimplemented here rather than taken as a dependency on an
    /// internal type.
    /// </summary>
    private static string CombineClassNames(IReadOnlyDictionary<string, object>? additionalAttributes, string computed)
    {
        if (additionalAttributes is null || !additionalAttributes.TryGetValue("class", out var splatted))
        {
            return computed;
        }

        var splattedClass = Convert.ToString(splatted, CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(splattedClass))
        {
            return computed;
        }

        return computed.Length == 0 ? splattedClass : $"{splattedClass} {computed}";
    }

    private void OnEngineStateChanged() => _ = InvokeAsync(StateHasChanged);

    /// <summary>
    /// Runs <see cref="DisposeCore"/>, then releases the field registration and the engine
    /// subscription. Deliberately not virtual: the base's cleanup is not a derived control's to
    /// forget, so a removed field cannot stay revealed because someone missed a base call.
    /// </summary>
    public void Dispose()
    {
        DisposeCore();
        _binding.Dispose();
    }

    /// <summary>
    /// Releases resources a derived control owns — a JS module, a timer, a subscription. Called by
    /// <see cref="Dispose"/> before the base releases the field registration and engine
    /// subscription, and doing nothing by default. A derived control implementing
    /// <see cref="IAsyncDisposable"/> owns the whole disposal path instead, because a component
    /// implementing both interfaces has only its async overload called: such a control must invoke
    /// <see cref="Dispose"/> from its <c>DisposeAsync</c>, or the registration is never released.
    /// </summary>
    protected virtual void DisposeCore()
    {
    }
}
