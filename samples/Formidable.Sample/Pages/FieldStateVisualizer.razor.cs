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
    // checks behind it (the validity check as the form opens, then each edit's own live check;
    // see docs/how-the-engine-works.md, the TrackFormValidity probe section) await the async
    // username/display-name checks, and when an answer lands the engine notifies the components
    // bound to it rather than the page, so without this subscription both would only catch up on
    // the next unrelated re-render. The kit's own inputs make this subscription for themselves.
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
