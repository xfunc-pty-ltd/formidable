using System.Globalization;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Base class for validated input components: wires field registration, engine state
/// subscription, touch/notify plumbing, computed CSS class, and aria attributes, so a concrete
/// input component only has to render markup and call <see cref="SetCurrentValueAsync"/> from its
/// change handler. <see cref="For"/> is (re-)read whenever the cascaded
/// <see cref="FormidableFormContext"/> is a new instance — including the first render and again
/// after a host such as <c>FormidableForm</c>/<c>FormidableValidator</c> swaps its model and
/// rebuilds its engine and registry — so the registration and the engine subscription always
/// target the currently-active context.
/// </summary>
/// <typeparam name="TValue">The field's value type.</typeparam>
public abstract class ValidatedInputBase<TValue> : ComponentBase, IDisposable
{
    private static readonly IReadOnlyDictionary<string, object> NoAriaAttributes = new Dictionary<string, object>();

    private readonly FormContextBinding _binding = new();

    /// <summary>The cascaded form context. Must be supplied by a FormidableForm/FormidableValidator ancestor.</summary>
    [CascadingParameter]
    protected FormidableFormContext? Context { get; set; }

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

    /// <inheritdoc />
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
    /// and notifies the EditContext so the engine's live validation pass runs. Call from a
    /// concrete input's change handler.
    /// </summary>
    protected async Task SetCurrentValueAsync(TValue? value)
    {
        Value = value;
        if (ValueChanged.HasDelegate)
        {
            await ValueChanged.InvokeAsync(value);
        }

        Context!.Engine.MarkTouched(Field);
        Context.EditContext.NotifyFieldChanged(Field);
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

    /// <inheritdoc />
    public void Dispose() => _binding.Dispose();
}
