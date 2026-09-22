using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>What <see cref="FormidableField{TValue}"/> hands its child content each render: the field's state, issues, class and the attributes a custom input binds.</summary>
public sealed class FormidableFieldContext
{
    private readonly IFormidableEngine _engine;

    internal FormidableFieldContext(
        IFormidableEngine engine,
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
        if (Requirement == FieldRequirement.Required)
        {
            inputAttributes["aria-required"] = "true";
        }
        InputAttributes = inputAttributes;
    }

    /// <summary>The field this context describes.</summary>
    public FieldIdentifier Field { get; }

    /// <summary>The element id for the field's input, as <see cref="FormidableFieldId.For(FieldIdentifier)"/> derives it.</summary>
    public string ElementId { get; }

    /// <summary>The field's current <see cref="FieldState"/>: touched, modified, being checked, the severities it carries, and <see cref="FieldState.WouldPassSubmit"/>.</summary>
    public FieldState State { get; }

    /// <summary>The state class string for the field, as <see cref="FormidableCss.Compute"/> builds it.</summary>
    public string CssClass { get; }

    /// <summary>The field's current issues, any severity.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>Whether the field has error-severity issues; bind it to the input's <c>aria-invalid</c>.</summary>
    public bool AriaInvalid { get; }

    /// <summary>The id of the field's message list while the field has issues, else <see langword="null"/>; bind it to the input's <c>aria-describedby</c>.</summary>
    /// <remarks>
    /// A single id, never a merged list: a control with a hint of its own composes both, hint
    /// first, as <c>aria-describedby="@($"my-hint {field.AriaDescribedBy}")"</c>.
    /// </remarks>
    public string? AriaDescribedBy { get; }

    /// <summary>How firmly the submit profile requires the field's value, as <see cref="IFormidableEngine.GetFieldRequirement"/> answers it; <see cref="FieldRequirement.Required"/> puts <c>aria-required</c> in <see cref="InputAttributes"/>.</summary>
    public FieldRequirement Requirement { get; }

    /// <summary>The attributes a custom input splats: <c>id</c>, <c>class</c>, and <c>aria-invalid</c>, <c>aria-describedby</c> and <c>aria-required</c> where each applies.</summary>
    /// <remarks>
    /// Splat them with <c>@attributes="field.InputAttributes"</c>. Wiring
    /// <see cref="NotifyChanged"/> stays the consumer's, because only the markup knows which
    /// event commits the value.
    /// </remarks>
    public IReadOnlyDictionary<string, object> InputAttributes { get; }

    /// <summary>Reports a committed value change from a custom input's change handler: the field is marked touched, engaged, and checked live from then on.</summary>
    public void NotifyChanged() => _engine.EditContext.NotifyFieldChanged(Field);

    /// <summary>Marks the field touched from a custom input's blur handler, without reporting a value change, so its classes update and no check runs.</summary>
    public void MarkTouched() => _engine.MarkTouched(Field);
}
