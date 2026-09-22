using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class AsyncRules
{
    private readonly Handle _handle = new();
    private string _status = string.Empty;

    private void HandleValid() => _status = "Submitted — username checks passed.";
}
