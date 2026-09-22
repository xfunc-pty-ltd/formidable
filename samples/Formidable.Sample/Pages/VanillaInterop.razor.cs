using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class VanillaInterop
{
    private readonly GadgetOrder _order = new();
    private string _status = string.Empty;

    private void HandleValid() => _status = "Submitted — native and Formidable inputs agreed.";
}
