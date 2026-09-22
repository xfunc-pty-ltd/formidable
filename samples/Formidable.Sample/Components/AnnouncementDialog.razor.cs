using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Formidable.Sample.Components;

public partial class AnnouncementDialog
{
    // How long the stylesheet takes to fade the panel in and out. A dialog library would raise
    // an opened and a closed event instead; this one waits out its own transition, which is the
    // same promise expressed with the tools a sample has: when CloseAsync completes, there is
    // nothing left on screen and focus has already gone back where it came from. The number is
    // hand-matched to app.css's .announcement transitions, and nothing binds the two: shorten
    // the transition there and this waits longer than it needs to, lengthen it and the close
    // reports back while the panel is still fading. Change both together.
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);

    private readonly string _headingId = $"announcement-heading-{Guid.NewGuid():N}";
    private ElementReference _panel;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    [Parameter]
    public string Heading { get; set; } = string.Empty;

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    // Where focus goes when the dialog closes. Handing focus back to whatever opened the dialog
    // is what a dialog owes its keyboard visitor, and it is also the step that decides whether a
    // focus move made too early survives: this one runs last, so it wins.
    [Parameter]
    public ElementReference ReturnFocusTo { get; set; }

    // Raised after a close the VISITOR asked for, which is the Close button and Escape. Those are
    // the two ways out that name no field, so the page that opened the dialog is the only thing
    // left that can say which FIELD the visitor goes to; a wired hand-back above has already left
    // them somewhere usable, so a page with no better answer can leave this unset. Closing on the
    // way to a field the visitor picked raises nothing: that route has a destination already.
    [Parameter]
    public EventCallback OnDismissed { get; set; }

    public bool Open { get; private set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // The half of aria-modal="true" that markup cannot state. The overlay hides the page
            // behind it and swallows its clicks, but a keyboard walks straight through into the
            // form, so the panel needs a Tab cycle of its own for the role to be telling the
            // truth. Registered once, on an element that lives for the page's lifetime.
            await Js.InvokeVoidAsync("formidableSample.trapTabWithin", _panel);
        }
    }

    public async Task OpenAsync()
    {
        if (Open)
        {
            return;
        }

        Open = true;
        StateHasChanged();
        await Task.Delay(FadeDuration);
        await _panel.FocusAsync();
    }

    public async Task CloseAsync()
    {
        if (!Open)
        {
            return;
        }

        Open = false;
        StateHasChanged();
        await Task.Delay(FadeDuration);

        // An unset reference has no element behind it, so there is nothing to hand focus to.
        if (!string.IsNullOrEmpty(ReturnFocusTo.Id))
        {
            await ReturnFocusTo.FocusAsync();
        }
    }

    // The visitor's own way out, from the button and from the key alike, so the two cannot drift
    // apart. The guard is what keeps a second press during the fade from announcing a dismissal
    // the first one already announced: CloseAsync has the same early return, and without one here
    // the callback would still run behind it.
    private async Task DismissAsync()
    {
        if (!Open)
        {
            return;
        }

        await CloseAsync();
        await OnDismissed.InvokeAsync();
    }

    // Escape is handled here and Tab is handled in the script, because the two need different
    // things: closing is state and an await, which belong in C#, while cycling Tab has to cancel
    // the browser's own move, and whether a Blazor handler prevents the default is fixed at
    // render time rather than decided per key.
    private async Task HandleKeyAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            await DismissAsync();
        }
    }
}
