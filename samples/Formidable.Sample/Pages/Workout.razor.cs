using System.Net.Http.Json;
using System.Text.Json;
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
            issue.Path.StartsWith("Sessions[", StringComparison.Ordinal) ? true : null,
    };

    private FormidableForm<EventRegistration>? _form;
    private string _status = string.Empty;

    // Where a click on the Include-catering entry lands once the field it names has left the
    // page: the checkbox is the control that governs the field's presence, so it is what answers
    // the click when the field itself no longer can.
    private ElementReference _includeCateringInput;

    private IFormidableEngine? _subscribedEngine;

    private FieldIdentifier VenueRegionField => new(_registration, nameof(EventRegistration.VenueRegion));

    // The focus service addresses a field by its id and nothing else, so a field this page renders
    // itself has to render that id too. The native venue input is the one field on this page with
    // no wrapper to do it for it; the model-level field the defensive gate reports under has no
    // input at all, but FormidableForm renders that id itself, on the form element.
    private string VenueRegionId => FormidableFieldId.For(VenueRegionField);

    // The id of the element listing this field's messages, shared by aria-describedby and the
    // native ValidationMessage's own id so a wrapped input's contract holds for a hand-rolled one.
    private string VenueRegionMessagesId => FormidableFieldId.MessagesFor(VenueRegionField);

    // The attribute that names it is conditional where the id itself is not. A native
    // ValidationMessage renders one <div> per message and nothing at all when there are none, so
    // the id above names something only while the field has a message; describing by it any
    // earlier points the screen reader at an element that is not there. A wrapped input is never
    // asked the question, because FormidableFieldMessage renders its list whether or not it holds
    // anything. The EditContext is what that component reads, so asking it is asking the same
    // question the component answers.
    private string? VenueRegionAriaDescribedBy =>
        _form?.Engine?.EditContext.GetValidationMessages(VenueRegionField).Any() == true
            ? VenueRegionMessagesId
            : null;

    // A wrapped input takes aria-invalid from its field context; a native one has no context, so
    // the page reads the same state off the engine. Null renders no attribute at all, which is
    // what a field with nothing to complain about must have.
    private string? VenueRegionAriaInvalid =>
        _form?.Engine?.GetFieldState(VenueRegionField).HasErrors == true ? "true" : null;

    // Requiredness crosses the seam the same way: a wrapped input takes aria-required from its
    // field context, and a native one has no context to ask, so the page reads what the submit
    // profile demands off the engine and renders the attribute the kit's inputs would.
    private string? VenueRegionAriaRequired =>
        _form?.Engine?.GetFieldRequirement(VenueRegionField) == FieldRequirement.Required ? "true" : null;

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

        // A 400 is not a promise that the body came from the endpoint: a proxy or gateway in
        // front of it answers with its own HTML page or its own JSON, the parse reads the
        // Content-Type header's character set as well as the body, and the JSON literal null
        // deserializes to nothing at all. A rejection the page cannot read is still a rejection,
        // not an exception the visitor should meet.
        FormidableValidationProblem? problem;
        try
        {
            // The generated metadata, not the plain generic overload: publishing a WebAssembly
            // app in Release runs the trimmer, and trimmed output cannot deserialize the type by
            // reflection. The guard below catches that refusal too rather than let it reach the
            // visitor.
            problem = await response.Content.ReadFromJsonAsync(
                FormidableValidationProblemJsonContext.Default.FormidableValidationProblem);
        }
        catch (Exception ex)
            when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            problem = null;
        }

        if (problem is null)
        {
            // The last verdict stays on screen, since an unreadable response is no evidence that
            // it stopped being true - at the cost of leaving an old reason standing when a
            // corrected resubmission is what came back unreadable. Clearing it instead is
            // ApplyServerIssues with an empty sequence.
            _status = "Rejected — but the response is not a verdict this page can read.";
            return;
        }

        // Each call replaces the previous server verdict rather than adding to it, so correcting
        // the coupon and resubmitting cannot leave the old rejection behind.
        _form!.ApplyServerIssues(problem);
        _status = "The server rejected the registration — see the messages above.";
    }

    private void ToggleCatering(ChangeEventArgs args, FormidableFieldContext field)
    {
        _registration.IncludeCatering = args.Value is true;

        // The engine only sees a change it is told about: mutating the model silently would leave
        // the live check and the whole-form re-check running against a state the form never
        // announced.
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

    // FormidableSummary calls this when a clicked issue's element does not take focus, and
    // FormidableForm calls it the same way when its own blocked-submit auto-focus misses. Two
    // reasons an element can be missing, two different answers:
    //   - a session row outside Virtualize's render window still has a model, just no element
    //     yet: scroll the panel to the row's approximate offset, give Virtualize a moment to
    //     render it, then let the caller retry the focus. The retry's own scrollIntoView centres
    //     the row exactly, so the row height only needs to be close.
    //   - the dietary-notes or catering-headcount field, unticked out of the page by its own
    //     checkbox, has nowhere to reappear: retrying would miss again by construction, so this
    //     answers the click itself, on the checkbox that governs the field's presence, and tells
    //     the caller not to retry.
    private async ValueTask<bool> RecoverMissedFocusAsync(FieldIdentifier field)
    {
        if (field.Model is SessionPick session)
        {
            var index = _registration.Sessions.IndexOf(session);
            if (index < 0)
            {
                return false;
            }

            await Js.InvokeVoidAsync("formidableSample.scrollPanelTo", ".scroll-panel", index * SessionRowHeight);
            await Task.Delay(120);
            return true;
        }

        // Matches on the field name alone, with no check that field.Model is _registration: this
        // page has only the one model, and no other field on it is named DietaryNotes or
        // CateringHeadcount, so the name alone already identifies the field uniquely. A second
        // model sharing either name would need the model checked too.
        if (!_registration.IncludeCatering
            && field.FieldName is nameof(EventRegistration.DietaryNotes) or nameof(EventRegistration.CateringHeadcount))
        {
            await _includeCateringInput.FocusAsync();
            return false;
        }

        return false;
    }

    // The engine notifies the components bound to it, not the page, so the aria-invalid above
    // would otherwise be one interaction stale: right at submit, then still "true" through the
    // check that cleared the error. The kit's own components subscribe for exactly this reason.
    // This page's model never swaps, so the first engine it sees is the only one.
    protected override void OnAfterRender(bool firstRender)
    {
        if (_subscribedEngine is null && _form?.Engine is { } engine)
        {
            _subscribedEngine = engine;
            engine.StateChanged += OnEngineStateChanged;
        }
    }

    private void OnEngineStateChanged(object? sender, FormidableStateChangedEventArgs e) =>
        _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        if (_subscribedEngine is not null)
        {
            _subscribedEngine.StateChanged -= OnEngineStateChanged;
        }
    }
}
