using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace Formidable.Blazor;

/// <summary>The JS-backed <see cref="IFormidableFieldOrderService"/>: sends each field's element id to formidable.js and maps the document-ordered answer back to fields, keyed by id.</summary>
// The browser is the only thing that knows where an element sits, so the ids cross the interop
// boundary and come back sorted by compareDocumentPosition.
internal sealed class FormidableFieldOrderService : FormidableJsBackedService, IFormidableFieldOrderService
{
    public FormidableFieldOrderService(IJSRuntime jsRuntime) : base(jsRuntime)
    {
    }

    public async ValueTask<IReadOnlyList<FieldIdentifier>?> OrderAsync(IReadOnlyList<FieldIdentifier> fields)
    {
        var ids = new string[fields.Count];
        var byId = new Dictionary<string, FieldIdentifier>(fields.Count, StringComparer.Ordinal);
        for (var i = 0; i < fields.Count; i++)
        {
            var id = FormidableFieldId.For(fields[i]);
            ids[i] = id;
            byId[id] = fields[i];
        }

        // Wrapped as the argument array itself: the ids are ONE argument — the list the JS side
        // iterates — and a string[] handed to a params object?[] IS that array, so the ids would
        // otherwise arrive spread across a separate parameter each.
        //
        // No ConfigureAwait(false) here — this is a component-layer service, and the accurate
        // precedent is FormidableJsModule, whose own awaits all keep the renderer's context
        // the same way (FormidableFocusService and FormidableDomValueSync have no awaits at
        // all, so they demonstrate nothing about context capture). The result assembly below
        // touches no component state, and keeping the context is what lets a later addition
        // that does stay safe rather than becoming a Server-only race no test would catch.
        var ordered = await Module
            .InvokeAsync<IReadOnlyList<string>>("orderFields", new object?[] { ids });

        // The answer crosses a deserialization boundary, so it can arrive as nothing at all
        // whatever the signature promises — and nothing at all is not an order.
        if (ordered is null)
        {
            return null;
        }

        var result = new List<FieldIdentifier>(ordered.Count);
        foreach (var id in ordered)
        {
            // The ids that come back are the ids that went out, so a miss is an answer about
            // something this was never asked about, and there is no field to report it for.
            if (byId.TryGetValue(id, out var field))
            {
                result.Add(field);
            }
        }

        return result;
    }
}
