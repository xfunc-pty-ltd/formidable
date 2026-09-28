using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor.Tests.Fixtures;

/// <summary>
/// Stands in for the focus service that calls JavaScript. It records every request, and answers
/// each one with <see cref="Lands"/>.
/// </summary>
public sealed class RecordingFocusService : IFormidableFocusService
{
    /// <summary>The fields focus was asked for, one entry per request, in order.</summary>
    public List<FieldIdentifier> Requests { get; } = [];

    /// <summary>
    /// What every request answers, where true means the field's element took focus. Set it to
    /// false to stand in for an element that is missing or will not take focus.
    /// </summary>
    public bool Lands { get; set; } = true;

    /// <summary>
    /// Runs inside each request, before it answers. A test that reads the page here sees it as it
    /// was when focus was asked for, not as it is once the call that started the request (a
    /// blocked submit, say) has returned.
    /// </summary>
    public Action<FieldIdentifier>? OnFocus { get; set; }

    public ValueTask<bool> FocusAsync(FieldIdentifier field)
    {
        Requests.Add(field);
        OnFocus?.Invoke(field);
        return ValueTask.FromResult(Lands);
    }
}
