using Microsoft.AspNetCore.Components.Forms;

namespace Formidable.Blazor.Tests.Fixtures;

/// <summary>
/// Records every <see cref="IFormidableFocusService"/> request, standing in for the JS-backed
/// service so a host test asserts against the seam instead of driving a browser. Registering it
/// before <c>AddFormidableBlazor()</c> is what puts it in place — that registration is a TryAdd.
/// </summary>
/// <remarks>
/// <see cref="OnFocus"/> is the difference from a plain spy, and it is what an ordering claim
/// needs: it runs INSIDE the focus request, so a test can read what the page had finished doing
/// at the moment focus was asked for rather than by the time the call that started it returned.
/// </remarks>
public sealed class RecordingFocusService : IFormidableFocusService
{
    /// <summary>The fields focus was requested for, one entry per call, in call order.</summary>
    public List<FieldIdentifier> Requests { get; } = [];

    /// <summary>What each request answers: false is the miss a fallback exists to recover.</summary>
    public bool Lands { get; set; } = true;

    /// <summary>Run during each request, before it answers.</summary>
    public Action<FieldIdentifier>? OnFocus { get; set; }

    public ValueTask<bool> FocusAsync(FieldIdentifier field)
    {
        Requests.Add(field);
        OnFocus?.Invoke(field);
        return ValueTask.FromResult(Lands);
    }
}
