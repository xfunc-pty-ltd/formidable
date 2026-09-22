using Formidable.Sample.Services;
using Microsoft.AspNetCore.Components;

namespace Formidable.Sample.Components;

// The per-page teaching pattern (walkthrough decision, 11 Aug): plain-language rules,
// a guided exercise, and the page's real embedded source - never a hand-copied snippet.
public partial class TeachingPanel
{
    [Parameter, EditorRequired] public RenderFragment Rules { get; set; } = default!;
    [Parameter, EditorRequired] public RenderFragment TryIt { get; set; } = default!;
    [Parameter] public string[]? CodeFiles { get; set; }
    [Inject] private SampleSourceReader Source { get; set; } = default!;
}
