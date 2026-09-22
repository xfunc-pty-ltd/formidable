using Formidable.Blazor;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components;

namespace Formidable.Sample.Pages;

public partial class ForeignControl
{
    private readonly GadgetOrder _order = new() { Nickname = "sample" };
    private string _status = string.Empty;

    private void OnColourChanged(ChangeEventArgs args, FormidableFieldContext field)
    {
        _order.Colour = args.Value?.ToString() ?? string.Empty;
        field.NotifyChanged();
    }

    private void HandleValid() => _status = "Submitted — the foreign control passed.";
}
