using System.Net.Http.Json;
using Formidable;
using Formidable.Blazor;
using Formidable.Sample.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace Formidable.Sample.Pages;

public partial class Workout : IDisposable
{
    // True only in a HOSTED_DEMO build (see HostedDemoApiHandler) - static readonly rather than
    // const so the razor's @if is a real runtime branch, not something the compiler could ever
    // flag as unreachable in the build where it is always false.
#if HOSTED_DEMO
    private static readonly bool IsHostedDemo = true;
#else
    private static readonly bool IsHostedDemo = false;
#endif

    // Matches app.css's fixed .scroll-panel .field height (102px) plus the field's own
    // 16px bottom margin — the true pitch from one row's top to the next, measured in a
    // real browser. The session title varies row to row, but at this panel's width it
    // never wraps, so the same fixed row height as Virtualized applies here too.
    private const float SessionRowHeight = 118f;

    private static readonly string[] SessionTracks =
        ["Keynote", "Workshop", "Panel", "Lightning talks", "Lab"];

    [Inject] private IModelValidator<EventRegistration> Validator { get; set; } = default!;

    [Inject] private HttpClient Http { get; set; } = default!;

    [Inject] private IJSRuntime Js { get; set; } = default!;

    private readonly EventRegistration _registration = new();

    // Sessions Virtualize has never rendered carry no registration, so render-gated disclosure
    // would hide their issues until the user scrolled to them. Forcing the collection visible
    // costs nothing: validation always runs the full in-memory model - only visibility is
    // render-gated.
    private readonly FormidableOptions _options = new()
    {
        DisclosureOverride = issue =>
            issue.Path.StartsWith("Sessions[", StringComparison.Ordinal) ? true : null
    };

    private FormidableForm<EventRegistration>? _form;
    private string _status = string.Empty;

    private IFormValidationEngine? _subscribedEngine;

    private FieldIdentifier VenueRegionField => new(_registration, nameof(EventRegistration.VenueRegion));

    // The focus service addresses a field by its id and nothing else, so a field this page renders
    // itself has to render that id too. The native venue input is the one field on this page with
    // no wrapper to do it for it; the model-level field the defensive gate reports under has no
    // input at all, but FormidableForm renders that id itself, on the form element.
    private string VenueRegionId => FormidableFieldId.For(VenueRegionField);

    // The id of the element listing this field's messages, shared by aria-describedby and the
    // native ValidationMessage's own id so a wrapped input's contract holds for a hand-rolled one.
    private string VenueRegionMessagesId => FormidableFieldId.MessagesFor(VenueRegionField);

    // A wrapped input takes aria-invalid from its field context; a native one has no context, so
    // the page reads the same state off the engine. Null renders no attribute at all, which is
    // what a field with nothing to complain about must have.
    private string? VenueRegionAriaInvalid =>
        _form?.Engine?.GetFieldState(VenueRegionField).HasErrors == true ? "true" : null;

    protected override void OnInitialized()
    {
        // Catering starts included so the dietary field is on screen for the happy path: its
        // rule is unconditional, and an issue on a field that is not rendered has nowhere to
        // show, so starting hidden would block every submit. Unticking it is the demo.
        _registration.IncludeCatering = true;

        // The tier select's initially-selected option: the form opens on the default tier rather
        // than on the blank choice, so a first submit does not complain about a decision the
        // visitor was never asked to make. Choosing the blank option is the demo.
        _registration.TicketTier = "General admission";

        // Long enough that Virtualize only ever renders a slice of it - a list that fits on
        // screen would leave the focus fallback below with nothing to prove.
        _registration.Sessions.AddRange(Enumerable.Range(1, 150).Select(i => new SessionPick
        {
            Id = i,
            Title = $"Session {i:D3}: {SessionTracks[i % SessionTracks.Length]}",
            Seats = "0"
        }));
    }

    private async Task SaveDraft()
    {
        var report = await Validator.ValidateAsync(_registration, ValidationProfile.Draft);
        _status = report.IsValid
            ? "Draft saved — no blocking issues."
            : $"Draft blocked: {string.Join("; ", report.Errors.Select(e => e.Message))}";
    }

    private void HandleBlocked() => _status = "Blocked by errors — fix them and resubmit.";

    private async Task SendToServer()
    {
        // Normalizing before the POST keeps the client's copy identical to what the server
        // validates (its filter normalizes too), so the coupon it judges is the coupon on screen.
        _registration.Normalize();

        var response = await Http.PostAsJsonAsync("/api/registrations/", _registration);
        if (response.IsSuccessStatusCode)
        {
            _status = "Submitted — registration accepted.";
            return;
        }

        var problem = await response.Content.ReadFromJsonAsync<FormidableValidationProblem>();

        // Each call replaces the previous server verdict rather than adding to it, so correcting
        // the coupon and resubmitting cannot leave the old rejection behind.
        _form!.ApplyServerIssues(problem!);
        _status = "The server rejected the registration — see the messages above.";
    }

    private void ToggleCatering(ChangeEventArgs args, FormidableFieldContext field)
    {
        _registration.IncludeCatering = args.Value is true;

        // The engine only sees a change it is told about: mutating the model silently would
        // leave the live and refresh passes running against a state the form never announced.
        field.NotifyChanged();
    }

    private void AddAttendee(FormidableFieldContext field)
    {
        _registration.Attendees.Add(new Attendee());

        // Same reasoning as ToggleCatering: Add/Remove mutate the list directly, so the engine
        // needs to be told the collection changed or the count-based rules go stale on screen.
        field.NotifyChanged();
    }

    private void RemoveAttendee(Attendee attendee, FormidableFieldContext field)
    {
        _registration.Attendees.Remove(attendee);
        field.NotifyChanged();
    }

    private void ChangeTicketTier(ChangeEventArgs args, FormidableFieldContext field)
    {
        _registration.TicketTier = args.Value?.ToString() ?? string.Empty;

        // A foreign control writes to the model itself, so the engine hears about the change
        // only when the field context tells it - the same contract as the catering checkbox.
        field.NotifyChanged();
    }

    // FormidableSummary calls this when a clicked issue's element is not in the DOM (a session outside
    // Virtualize's render window): scroll the panel to the row's approximate offset, give
    // Virtualize a moment to render it, then let the summary retry the focus. The retry's own
    // scrollIntoView centres the row exactly, so the row height only needs to be close.
    private async ValueTask<bool> ScrollToSessionAsync(FieldIdentifier field)
    {
        if (field.Model is not SessionPick session)
        {
            return false;
        }

        var index = _registration.Sessions.IndexOf(session);
        if (index < 0)
        {
            return false;
        }

        await Js.InvokeVoidAsync("formidableSample.scrollPanelTo", ".scroll-panel", index * SessionRowHeight);
        await Task.Delay(120);
        return true;
    }

    // The engine notifies the components bound to it, not the page, so the aria-invalid above
    // would otherwise be one interaction stale: right at submit, then still "true" through the
    // pass that cleared the error. The kit's own components subscribe for exactly this reason.
    // This page's model never swaps, so the first engine it sees is the only one.
    protected override void OnAfterRender(bool firstRender)
    {
        if (_subscribedEngine is null && _form?.Engine is { } engine)
        {
            _subscribedEngine = engine;
            engine.StateChanged += OnEngineStateChanged;
        }
    }

    private void OnEngineStateChanged() => _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        if (_subscribedEngine is not null)
        {
            _subscribedEngine.StateChanged -= OnEngineStateChanged;
        }
    }
}
