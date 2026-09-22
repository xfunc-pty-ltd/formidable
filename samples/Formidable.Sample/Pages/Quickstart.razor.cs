using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class Quickstart
{
    private readonly QuickContact _contact = new();
    private bool _submitted;

    private void HandleValid() => _submitted = true;
}
