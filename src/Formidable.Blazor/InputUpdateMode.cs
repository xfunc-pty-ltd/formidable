namespace Formidable.Blazor;

/// <summary>Which native DOM event a <see cref="ValidatedInputBase{TValue}"/>-derived input commits its value on.</summary>
public enum InputUpdateMode
{
    /// <summary>Commits on the <c>onchange</c> event (default) — fires when the element loses focus after a change.</summary>
    OnChange,

    /// <summary>Commits on the <c>oninput</c> event — fires on every keystroke.</summary>
    OnInput,
}
