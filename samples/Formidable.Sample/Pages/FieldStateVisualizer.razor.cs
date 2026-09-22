using Formidable.Blazor;
using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class FieldStateVisualizer : IDisposable
{
    private readonly Handle _handle = new();
    private readonly FormidableOptions _options = new() { TrackFormValidity = true };
    private FormidableForm<Handle>? _form;
    private IFormidableEngine? _subscribedEngine;
    private string _status = string.Empty;

    private void HandleValid() => _status = "Submitted — state flags above tell the story.";

    // The readout and the disabled attribute in the markup read IsFormValid off the engine. The
    // validity check behind it (see docs/how-the-engine-works.md, the TrackFormValidity probe
    // section) awaits the async username/display-name checks, and when its answer lands the
    // engine notifies the components bound to it rather than the page, so without this
    // subscription both would only catch up on the next unrelated re-render. The live check
    // beside it lands the same way, for the same reason; the kit's own inputs make this
    // subscription for themselves.
    protected override void OnAfterRender(bool firstRender)
    {
        if (_subscribedEngine is null && _form?.Engine is { } engine)
        {
            _subscribedEngine = engine;
            engine.StateChanged += OnEngineStateChanged;
        }
    }

    private void OnEngineStateChanged(object? sender, FormidableStateChangedEventArgs e) =>
        _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        if (_subscribedEngine is not null)
        {
            _subscribedEngine.StateChanged -= OnEngineStateChanged;
        }
    }
}
