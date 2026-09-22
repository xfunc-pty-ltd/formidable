using Formidable.Sample.Shared;

namespace Formidable.Sample.Pages;

public partial class FieldStateVisualizer
{
    private readonly Handle _handle = new();
    private string _status = string.Empty;

    private void HandleValid() => _status = "Submitted — state flags above tell the story.";
}
