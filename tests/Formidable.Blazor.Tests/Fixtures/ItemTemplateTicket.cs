namespace Formidable.Blazor.Tests.Fixtures;

/// <summary>A field, a field that is never rendered, and a collection, for the message-list template pins.</summary>
public sealed class ItemTemplateTicket
{
    public string Reference { get; set; } = string.Empty;

    public string Requester { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = [];
}
