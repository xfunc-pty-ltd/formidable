namespace Formidable.Blazor;

/// <summary>Which native event a <see cref="FormidableInputBase{TValue}"/>-derived input commits its value on, and when it tells the engine.</summary>
public enum InputUpdateMode
{
    /// <summary>Commits on the element's <c>change</c> event, which a text box fires when it loses focus after an edit. The default.</summary>
    OnChange,

    /// <summary>Commits on the element's <c>input</c> event, on every keystroke; a control with no distinct <c>input</c> event, such as a select, commits on <c>change</c>.</summary>
    OnInput,

    /// <summary>Commits on <c>change</c> but tells the engine on the next blur, and only when a commit happened, so a control firing <c>change</c> per segment is checked once the value settles.</summary>
    OnBlur,
}
