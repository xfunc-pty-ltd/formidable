using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class Quickstart
{
    private readonly QuickContact _contact = new();
    private string _status = string.Empty;

    private void HandleValid() => _status = $"Submitted — thanks, {_contact.Name}!";
}
