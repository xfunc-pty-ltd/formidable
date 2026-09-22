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
    private const float SessionRowHeight = 96f;

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
    // itself has to render that id too. The native venue input is one; the model-level field the
    // defensive gate reports under is the other, and it has no input at all - the form element
    // carries it.
    private string VenueRegionId => FormidableFieldId.For(VenueRegionField);

    private string FormGateId => FormidableFieldId.For(new FieldIdentifier(_registration, string.Empty));

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
        _form!.Engine!.ApplyServerIssues(problem!.ToIssues());
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

    // Change fires per segment keystroke on a native date input, so these only write the
    // model - NotifyChanged waits for @onblur, once the value has settled.
    private void ChangeEventDate(ChangeEventArgs args) =>
        _registration.EventDate = args.Value?.ToString() ?? string.Empty;

    private void ChangeEarlyBirdDeadline(ChangeEventArgs args) =>
        _registration.EarlyBirdDeadline = args.Value?.ToString() ?? string.Empty;

    private void ChangeTicketTier(ChangeEventArgs args, FormidableFieldContext field)
    {
        _registration.TicketTier = args.Value?.ToString() ?? string.Empty;

        // A foreign control writes to the model itself, so the engine hears about the change
        // only when the field context tells it - the same contract as the catering checkbox.
        field.NotifyChanged();
    }

    // FormSummary calls this when a clicked issue's element is not in the DOM (a session outside
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
