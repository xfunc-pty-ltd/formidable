using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>
/// Per-field context handed to <see cref="FormidableField{TValue}"/>'s child content each render:
/// the field's current state, issues, computed CSS class, and the aria ids a custom input should
/// bind to. Lets any UI library build its own field markup without re-deriving engine wiring.
/// </summary>
public sealed class FormidableFieldContext
{
    private readonly IFormValidationEngine _engine;

    internal FormidableFieldContext(
        IFormValidationEngine engine,
        FieldIdentifier field,
        string elementId,
        FieldState state,
        string cssClass,
        IReadOnlyList<ValidationIssue> issues)
    {
        _engine = engine;
        Field = field;
        ElementId = elementId;
        State = state;
        CssClass = cssClass;
        Issues = issues;
        AriaInvalid = state.HasErrors;
        AriaDescribedBy = issues.Count > 0 ? FormidableFieldId.MessagesFor(elementId) : null;
        Requirement = engine.GetFieldRequirement(field);

        var inputAttributes = new Dictionary<string, object>(5)
        {
            ["id"] = elementId,
            ["class"] = cssClass,
        };
        if (AriaInvalid)
        {
            inputAttributes["aria-invalid"] = "true";
        }
        if (AriaDescribedBy is not null)
        {
            inputAttributes["aria-describedby"] = AriaDescribedBy;
        }
        if (Requirement == RuleRequirement.Required)
        {
            inputAttributes["aria-required"] = "true";
        }
        InputAttributes = inputAttributes;
    }

    /// <summary>The field this context describes.</summary>
    public FieldIdentifier Field { get; }

    /// <summary>The deterministic element id for the field's input (see <see cref="FormidableFieldId"/>).</summary>
    public string ElementId { get; }

    /// <summary>The field's current state (touched, modified, validating, errors, warnings).</summary>
    public FieldState State { get; }

    /// <summary>The computed CSS class string for the field's current state (see <see cref="FormidableCss"/>).</summary>
    public string CssClass { get; }

    /// <summary>The field's current issues, any severity.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>True when the field currently has error-severity issues — bind to the input's <c>aria-invalid</c>.</summary>
    public bool AriaInvalid { get; }

    /// <summary>
    /// The id of the element holding the field's messages, or null when it has none — bind to
    /// the input's <c>aria-describedby</c>. It is
    /// <see cref="FormidableFieldId.MessagesFor(Microsoft.AspNetCore.Components.Forms.FieldIdentifier)"/>
    /// for <see cref="Field"/>, the same id the field's message list renders on itself.
    /// </summary>
    public string? AriaDescribedBy { get; }

    /// <summary>
    /// How firmly the submit profile's rules demand that the field carry a value — see
    /// <see cref="IFormValidationEngine.GetFieldRequirement"/> for where the answer comes from
    /// and what it cannot see. <see cref="RuleRequirement.Required"/> is what
    /// <c>FormidableRequiredIndicator</c> marks and what puts <c>aria-required</c> in
    /// <see cref="InputAttributes"/>; a control rendering its own marker reads all three values
    /// here and decides for itself, which is the only way to draw anything for
    /// <see cref="RuleRequirement.ConditionallyRequired"/>.
    /// </summary>
    public RuleRequirement Requirement { get; }

    /// <summary>
    /// The one-splat seam for a foreign control: <c>id</c>, <c>class</c>, and — only when
    /// applicable — <c>aria-invalid</c>, <c>aria-describedby</c> and <c>aria-required</c>,
    /// bundled exactly as <see cref="ElementId"/>, <see cref="CssClass"/>,
    /// <see cref="AriaInvalid"/>, <see cref="AriaDescribedBy"/> and <see cref="Requirement"/>
    /// already report them. Splat it onto the control with
    /// <c>@attributes="field.InputAttributes"</c>; <see cref="NotifyChanged"/> is still the
    /// consumer's own wiring, since only the consumer's markup knows which native event commits
    /// the control's value.
    /// </summary>
    public IReadOnlyDictionary<string, object> InputAttributes { get; }

    /// <summary>
    /// Notifies the EditContext that the field changed, which is what marks it touched and runs the
    /// engine's live validation pass — call from a custom input's change handler. Calling it is the
    /// consumer's statement that a committed value change happened: it engages the field, and every
    /// subsequent live pass answers an engaged field's verdict, not only the pass this call
    /// triggers.
    /// </summary>
    public void NotifyChanged() => _engine.EditContext.NotifyFieldChanged(Field);

    /// <summary>Marks the field touched without notifying a value change — call from a custom input's blur/focus-out handler.</summary>
    public void MarkTouched() => _engine.MarkTouched(Field);
}
