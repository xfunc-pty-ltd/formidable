namespace Formidable.Blazor;

/// <summary>Which native DOM event a <see cref="FormidableInputBase{TValue}"/>-derived input commits its value on.</summary>
public enum InputUpdateMode
{
    /// <summary>Commits on the <c>onchange</c> event (default) — fires when the element loses focus after a change.</summary>
    OnChange,

    /// <summary>Commits on the <c>oninput</c> event — fires on every keystroke.</summary>
    OnInput,

    /// <summary>
    /// Commits the value on the <c>change</c> event, same as <see cref="OnChange"/>, but defers
    /// the engine's field-change notification instead of firing it as part of the same commit:
    /// delivers the notification on blur when a value commit has occurred since the last one, and
    /// delivers nothing on a blur with no committed change — tabbing through the field starts no
    /// live pass at all. For a native control whose <c>change</c> event fires
    /// more than once per logical edit — a date input firing per date-segment, a number input
    /// firing per spinner click — so the live pass a notification starts waits until the value has
    /// actually settled instead of running on a half-typed value.
    /// </summary>
    OnBlur,
}
