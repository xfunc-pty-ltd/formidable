using Formidable.Sample.Services;
using Microsoft.AspNetCore.Components;

namespace Formidable.Sample.Components;

// The per-page teaching pattern. The component renders the whole lesson: a "lesson" wrapper
// holding the panel and then the page's demo. The panel is a complementary landmark named "About
// this page". It reads Try it (the steps), The rules (what the validator demands), How it works
// (the explanations, closed until opened) and Show the code (the page's real embedded source,
// never a hand-copied snippet). The stylesheet sets the demo beside the panel on a wide window and
// below it on a narrow one. Every fragment is required, so a page that leaves one out fails to
// build; only the list of code files is optional.
public partial class TeachingPanel
{
    [Parameter, EditorRequired] public RenderFragment Rules { get; set; } = default!;
    [Parameter, EditorRequired] public RenderFragment TryIt { get; set; } = default!;
    [Parameter, EditorRequired] public RenderFragment HowItWorks { get; set; } = default!;
    [Parameter, EditorRequired] public RenderFragment Demo { get; set; } = default!;
    [Parameter] public string[]? CodeFiles { get; set; }
    [Inject] private SampleSourceReader Source { get; set; } = default!;
}
