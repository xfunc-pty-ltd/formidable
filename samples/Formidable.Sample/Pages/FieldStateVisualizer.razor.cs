using Formidable.Blazor;
using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class FieldStateVisualizer : IDisposable
{
    private readonly Handle _handle = new();
    private readonly FormidableOptions _options = new() { TrackFormValidity = true };
    private FormidableForm<Handle>? _form;
    private IFormValidationEngine? _subscribedEngine;
    private string _status = string.Empty;

    private void HandleValid() => _status = "Submitted — state flags above tell the story.";

    // TrackFormValidity's probe lands its verdicts and the IsFormValid flip off the render
    // sync context (it awaits the async username/display-name checks), so without this the
    // readout, the disabled attribute, and the valid borders fed by the store's answer would
    // only catch up on the next unrelated re-render. The live pass beside it lands the same
    // way, for the same reason.
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
