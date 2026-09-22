using Formidable.Blazor;
using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class SummaryShape
{
    private readonly SupportTicket _ticket = new();

    private bool _nameTheField;
    private bool _groupByField;
    private bool _capped;
    private bool _reveal;
    private string _status = string.Empty;

    // One method behind both templates, which is the point of handing the overflow fragment the
    // entries: a held-back entry is the same VisibleIssue a shown one is, so the expander renders
    // it exactly as the list would have. The name is nullable — an error mapped from a server's
    // ProblemDetails body arrives with a path and no name — so the path stands in where an issue
    // has one.
    private string EntryText(VisibleIssue entry) =>
        _nameTheField ? entry.Issue.DisplayName ?? entry.Issue.Path : entry.Issue.Message;

    private void HandleValid() => _status = "Submitted — the ticket is with the support queue.";
}
