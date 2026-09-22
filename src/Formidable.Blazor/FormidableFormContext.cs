using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Cascaded to field components: the engine view for one Formidable form.</summary>
public sealed class FormidableFormContext
{
    /// <summary>Wraps an engine for cascading.</summary>
    /// <remarks>
    /// The two shipped roots — <see cref="FormidableForm{TModel}"/> and
    /// <see cref="FormidableValidator{TModel}"/> — create and cascade this context, and they
    /// are the only supported hosts for one. Constructing it directly is supported for tests
    /// that cascade a context around an engine or a double; a hand-assembled root built this
    /// way never learns of rendered-field-set changes, because the reconciliation and ordering
    /// seams are internal, wired by the shipped hosts.
    /// </remarks>
    public FormidableFormContext(IFormValidationEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Engine = engine;
    }

    /// <summary>The form's validation engine.</summary>
    public IFormValidationEngine Engine { get; }

    /// <summary>The form's edit context.</summary>
    public EditContext EditContext => Engine.EditContext;

    /// <summary>The disclosure registry field components register with.</summary>
    public FieldRegistry Registry => Engine.Registry;
}
