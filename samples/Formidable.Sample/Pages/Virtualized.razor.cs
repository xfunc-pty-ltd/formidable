using Formidable.Blazor;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace Formidable.Sample.Pages;

public partial class Virtualized
{
    private const float RowHeight = 96f;

    [Inject] private IJSRuntime Js { get; set; } = default!;

    private readonly GadgetOrder _order = new()
    {
        Colour = "Red",
        Nickname = "bulk",
        Gadgets = [.. Enumerable.Range(1, 200).Select(i => new Gadget { Serial = i % 7 == 0 ? string.Empty : $"SN-{i:0000}" })]
    };

    // Rows Virtualize has never rendered carry no registration, so render-gated
    // disclosure would hide their issues until the user scrolled past them. Forcing the
    // collection visible costs nothing: validation always runs the full in-memory model -
    // only visibility is render-gated.
    private readonly FormidableOptions _options = new()
    {
        DisclosureOverride = issue =>
            issue.Path.StartsWith("Gadgets[", StringComparison.Ordinal) ? true : null
    };

    private string _status = string.Empty;

    private void HandleValid() => _status = "Submitted — all 200 serials present.";

    // FormidableSummary calls this when a clicked issue's element is not in the DOM (row outside
    // the virtualized render window): scroll the panel to the row's approximate offset, give
    // Virtualize a moment to render it, then let the summary retry the focus. The retry's own
    // scrollIntoView centres the row exactly, so RowHeight only needs to be close, not perfect.
    // A production consumer might poll for the element instead of a fixed delay.
    private async ValueTask<bool> ScrollToRowAsync(FieldIdentifier field)
    {
        if (field.Model is not Gadget gadget)
        {
            return false;
        }

        var index = _order.Gadgets.IndexOf(gadget);
        if (index < 0)
        {
            return false;
        }

        await Js.InvokeVoidAsync("formidableSample.scrollPanelTo", ".scroll-panel", index * RowHeight);
        await Task.Delay(120);
        return true;
    }
}
