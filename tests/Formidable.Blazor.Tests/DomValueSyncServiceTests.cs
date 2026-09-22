using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Pins the JS-backed <see cref="IFormidableDomValueSync"/> the way FocusServiceTests pins the
/// focus service: registered by <c>AddFormidableBlazor</c>, backed by the shared formidable.js
/// module, forwarding the element id and formatted value as-is.
/// </summary>
public class DomValueSyncServiceTests : BunitContext
{
    [Fact]
    public async Task Sync_invokes_the_module_function_with_the_id_and_value()
    {
        Services.AddFormidableBlazor();
        var module = JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js");
        module.SetupVoid("syncValue", _ => true).SetVoidResult();

        var sync = Services.GetRequiredService<IFormidableDomValueSync>();
        await sync.SyncValueAsync("field-id", "42");

        var invocation = module.VerifyInvoke("syncValue");
        Assert.Equal(["field-id", "42"], invocation.Arguments);
    }
}
