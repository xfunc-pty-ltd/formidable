using System.Globalization;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Formidable.Sample.Pages;

public partial class Localization
{
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private readonly LocalizedProfile _profile = new();
    private string _status = string.Empty;

    private static string Culture => CultureInfo.CurrentUICulture.Name;

    // WebAssembly fixes the culture at startup, so switching it requires a reload - the standard
    // pattern: store the choice where it survives the reload, then force-load the page so
    // Program.cs boots on the new culture and pulls its satellite assemblies.
    private async Task SwitchCulture(ChangeEventArgs args)
    {
        if (args.Value is not string culture || culture == Culture)
        {
            return;
        }

        await Js.InvokeVoidAsync("formidableSample.setCulture", culture);
        Navigation.NavigateTo(Navigation.Uri, forceLoad: true);
    }

    private void HandleValid() => _status = $"Accepted — {_profile.FullName}, age {_profile.Age}.";
}
