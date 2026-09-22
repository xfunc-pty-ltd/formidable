using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor;

/// <summary>Cascaded to field components: the engine view for one Formidable form.</summary>
public sealed class FormidableFormContext
{
    /// <summary>Wraps an engine for cascading.</summary>
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
