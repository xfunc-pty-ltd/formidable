using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class AsyncRules : IDisposable
{
    private readonly Handle _handle = new();
    private string _status = string.Empty;

    private static int DelayMs
    {
        get => HandleValidator.SimulatedDelayMs;
        set => HandleValidator.SimulatedDelayMs = value;
    }

    private void HandleValid() => _status = "Submitted — username checks passed.";

    // SimulatedDelayMs is a static shared by every page that runs Handle's async rules (e.g.
    // field-state); restore the default on leaving so this page's slider doesn't strand
    // other pages' async timing at whatever value was last dragged here.
    public void Dispose() => HandleValidator.SimulatedDelayMs = 600;
}
