using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Render ceilings on a form with the sample's largest page's composition (the Workout page,
/// component for component, with a fixed window of ten session rows where Virtualize would be).
/// One committed edit, before or after a submit, re-renders the edited field's components and
/// nothing else (under a row-key check, the wrappers' contents too); a blocked submit re-renders
/// every field for the form's own renders, and each field the submit profile declares a rule for
/// twice more for its pending flips. bUnit's RenderCount on a
/// component that has children counts their updates too (its UpdateParents walk), so every
/// assertion here reads leaf components: inputs, messages, the summary and the consumer children,
/// never a wrapper, the form or the host.
/// </summary>
public class WorkoutShapedRenderCeilingTests : BunitContext
{
    private const int SessionRowsRendered = 10;
    private static readonly FormidableOptions Options = new();
    private readonly FakeTimeProvider _time = new();

    public WorkoutShapedRenderCeilingTests()
    {
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<IFormidableFocusService>(new RecordingFocusService());
        Services.AddSingleton<IFormidableFieldOrderService>(new RecordingFieldOrderService());
        Services.AddSingleton<IFormidableDomValueSync>(new RecordingDomValueSync());
        Services.AddFormidableBlazor();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // One committed edit names the edited field alone, so only its input and message render;
    // every other input, message and consumer child keeps its count.
    [Fact]
    public async Task One_committed_edit_renders_only_the_edited_fields_components_and_the_summary()
    {
        var cut = await RenderWorkoutShapedFormAsync();
        var engine = cut.FindComponent<FormidableForm<Registration>>().Instance.Engine!;
        var probe = cut.FindComponent<PublishProbe>().Instance;
        var summary = cut.FindComponent<FormidableSummary>();
        var collectionMessage = cut.FindComponent<FormidableCollectionMessage<List<Attendee>>>();
        var before = RenderCounts(cut);
        var publishesBefore = probe.Publishes;

        await cut.InvokeAsync(() => cut.Find("input[id$='-description']").Change("a note"));
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });
        var publishes = probe.Publishes - publishesBefore;

        // The rendered id ends in the sanitised field name (FormidableFieldId.For), which is how
        // the components are told apart without reaching a protected member.
        var descriptionInput = cut.FindComponents<FormidableInputText>().Single(c => c.Markup.Contains("-description\""));
        var descriptionMessage = cut.FindComponents<FormidableFieldMessage<string>>().Single(c => c.Markup.Contains("-description-messages"));

        Assert.InRange(descriptionInput.RenderCount - before[descriptionInput.ComponentId], 1, 2);
        Assert.InRange(descriptionMessage.RenderCount - before[descriptionMessage.ComponentId], 1, 2);
        foreach (var component in cut.FindComponents<FormidableInputText>().Where(c => c.ComponentId != descriptionInput.ComponentId))
        {
            Assert.Equal(before[component.ComponentId], component.RenderCount);
        }
        foreach (var component in cut.FindComponents<FormidableFieldMessage<string>>().Where(c => c.ComponentId != descriptionMessage.ComponentId))
        {
            Assert.Equal(before[component.ComponentId], component.RenderCount);
        }
        foreach (var component in cut.FindComponents<WrapperChild>())
        {
            Assert.Equal(before[component.ComponentId], component.RenderCount);
        }
        Assert.InRange(summary.RenderCount - before[summary.ComponentId], 0, publishes);
        Assert.Equal(before[collectionMessage.ComponentId], collectionMessage.RenderCount);

        await Services.DisposeAsync();
    }

    // A submit's "checking" covers the fields the submit profile declares a rule for, so the
    // pending class turns on and off for each of those (two real view moves per field) and for no
    // other. The form renders at the submit's start, after the check and at its end, and each of
    // those re-sets the components under its fragment. Description and Coupon code have no rule,
    // and the catering checkbox's wrapper draws a field with none, so their leaves render for the
    // form's renders alone. That is 222 leaf renders: 96 inputs (18 at five, 2 at three), 101
    // messages (19 at five, 2 at three), 18 wrapper children (3 at five, 1 at three), the
    // collection message's 5 and the summary's 2. A scope covering every field rendered 232.
    [Fact]
    public async Task A_blocked_submit_renders_the_declared_fields_for_their_pending_flips_and_every_field_for_the_forms_own_renders()
    {
        var cut = await RenderWorkoutShapedFormAsync();
        var form = cut.FindComponent<FormidableForm<Registration>>().Instance;
        var formRenders = cut.FindComponent<FormRenderCounter>();
        var summary = cut.FindComponent<FormidableSummary>();
        var collectionMessage = cut.FindComponent<FormidableCollectionMessage<List<Attendee>>>();
        var before = RenderCounts(cut);

        await cut.InvokeAsync(() => form.SubmitAsync());
        cut.WaitForState(() => !form.Engine!.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });

        var ownRenders = formRenders.RenderCount - before[formRenders.ComponentId];
        Assert.True(ownRenders > 0, "the form did not render at the submit");

        // The rendered ids end in the sanitised field name, as in the edit ceilings above.
        static bool HasNoRule(string markup) => markup.Contains("-description") || markup.Contains("-couponcode");

        var total = 0;
        foreach (var component in cut.FindComponents<FormidableInputText>())
        {
            var delta = component.RenderCount - before[component.ComponentId];
            Assert.Equal(HasNoRule(component.Markup) ? ownRenders : ownRenders + 2, delta);
            total += delta;
        }
        foreach (var component in cut.FindComponents<FormidableFieldMessage<string>>())
        {
            var delta = component.RenderCount - before[component.ComponentId];
            Assert.Equal(HasNoRule(component.Markup) ? ownRenders : ownRenders + 2, delta);
            total += delta;
        }
        foreach (var component in cut.FindComponents<WrapperChild>())
        {
            var delta = component.RenderCount - before[component.ComponentId];
            var noRule = component.Instance.Field!.Field.FieldName == nameof(Registration.IncludeCatering);
            Assert.Equal(noRule ? ownRenders : ownRenders + 2, delta);
            total += delta;
        }
        var collectionDelta = collectionMessage.RenderCount - before[collectionMessage.ComponentId];
        var summaryDelta = summary.RenderCount - before[summary.ComponentId];
        Assert.Equal(ownRenders + 2, collectionDelta);
        Assert.Equal(2, summaryDelta);
        Assert.Equal(222, total + collectionDelta + summaryDelta);

        await Services.DisposeAsync();
    }

    // After a submit, an edit re-checks the whole form once the refresh window closes. The edited
    // field has no rule of its own and was touched before the submit, so only its own state moves
    // across that edit's publishes: every other input and message keeps its count.
    [Fact]
    public async Task One_committed_edit_after_a_blocked_submit_renders_only_the_edited_fields_components_and_the_summary()
    {
        var cut = await RenderWorkoutShapedFormAsync();
        var form = cut.FindComponent<FormidableForm<Registration>>().Instance;
        var engine = form.Engine!;
        var probe = cut.FindComponent<PublishProbe>().Instance;
        var summary = cut.FindComponent<FormidableSummary>();
        var collectionMessage = cut.FindComponent<FormidableCollectionMessage<List<Attendee>>>();

        await cut.InvokeAsync(() => cut.Find("input[id$='-description']").Change("a note"));
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => form.SubmitAsync());
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });

        var before = RenderCounts(cut);
        var publishesBefore = probe.Publishes;
        await cut.InvokeAsync(() => cut.Find("input[id$='-description']").Change("a longer note"));
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => _time.Advance(Options.RefreshDebounce + TimeSpan.FromMilliseconds(1)));
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });
        // The live check publishes as it begins and as it ends, and the refresh at least once more.
        var publishes = probe.Publishes - publishesBefore;
        Assert.True(publishes >= 3, $"expected the live check's and the refresh's publishes, saw {publishes}");

        var descriptionInput = cut.FindComponents<FormidableInputText>().Single(c => c.Markup.Contains("-description\""));
        var descriptionMessage = cut.FindComponents<FormidableFieldMessage<string>>().Single(c => c.Markup.Contains("-description-messages"));
        Assert.InRange(descriptionInput.RenderCount - before[descriptionInput.ComponentId], 1, publishes);
        Assert.InRange(descriptionMessage.RenderCount - before[descriptionMessage.ComponentId], 1, publishes);
        Assert.InRange(summary.RenderCount - before[summary.ComponentId], 0, publishes);
        Assert.Equal(before[collectionMessage.ComponentId], collectionMessage.RenderCount);

        var rendered = new List<string>();
        foreach (var component in cut.FindComponents<FormidableInputText>().Where(c => c.ComponentId != descriptionInput.ComponentId))
        {
            Note(rendered, "input", component, before);
        }
        foreach (var component in cut.FindComponents<FormidableFieldMessage<string>>().Where(c => c.ComponentId != descriptionMessage.ComponentId))
        {
            Note(rendered, "message", component, before);
        }
        Assert.True(rendered.Count == 0, "components outside the edited field rendered: " + string.Join(", ", rendered));

        await Services.DisposeAsync();
    }

    // Under a row-key check a wrapper also answers a form-level change, and the check an edit
    // starts begins and ends as one, so a committed edit re-renders the four wrappers' contents
    // beside the edited field's own input and message, while every leaf outside a wrapper keeps
    // the field filter. That is 15 leaf renders: 4 inputs (the edited one, the wrapped contact
    // email and the attendee's two), 5 messages (those four fields' and the ticket tier's), the 4
    // wrapper children, the collection message and the summary. Answering every publish under a
    // check, as the whole-form exemption did, rendered every leaf once: 47.
    [Fact]
    public async Task One_committed_edit_under_a_row_key_check_renders_the_edited_fields_components_the_wrappers_contents_and_the_summary()
    {
        var cut = await RenderWorkoutShapedFormAsync(new FormidableOptions { VerifyRowKeys = true });
        var engine = cut.FindComponent<FormidableForm<Registration>>().Instance.Engine!;
        var before = RenderCounts(cut);

        await cut.InvokeAsync(() => cut.Find("input[id$='-description']").Change("a note"));
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });

        int Delta<T>()
            where T : IComponent =>
            cut.FindComponents<T>().Sum(c => c.RenderCount - before[c.ComponentId]);

        var inputs = Delta<FormidableInputText>();
        var messages = Delta<FormidableFieldMessage<string>>();
        var children = Delta<WrapperChild>();
        var collection = Delta<FormidableCollectionMessage<List<Attendee>>>();
        var summary = Delta<FormidableSummary>();
        Assert.Equal(4, inputs);
        Assert.Equal(5, messages);
        Assert.Equal(4, children);
        Assert.Equal(1, collection);
        Assert.Equal(1, summary);
        Assert.Equal(15, inputs + messages + children + collection + summary);

        await Services.DisposeAsync();
    }

    private static void Note<T>(List<string> rendered, string kind, IRenderedComponent<T> component, Dictionary<int, int> before)
        where T : IComponent
    {
        var delta = component.RenderCount - before[component.ComponentId];
        if (delta != 0)
        {
            rendered.Add($"{kind} #{component.ComponentId} rendered {delta} time(s)");
        }
    }

    /// <summary>
    /// Renders the form and makes one committed edit of a field the measured edits leave alone, so
    /// a ceiling measures from a form whose first check has run. That check gives every passing
    /// field its green at once, a one-time move the kit draws, and a window opened before it would
    /// count those renders against the edit under test.
    /// </summary>
    private async Task<IRenderedComponent<WorkoutShapedHost>> RenderWorkoutShapedFormAsync(FormidableOptions? options = null)
    {
        var registration = new Registration();
        registration.Attendees.Add(new Attendee());
        registration.Sessions.AddRange(Enumerable.Range(1, SessionRowsRendered).Select(i => new SessionPick { Id = i, Title = $"Session {i}", Seats = "0" }));

        var cut = Render<WorkoutShapedHost>(p => p
            .Add(x => x.Registration, registration)
            .Add(x => x.FormOptions, options ?? Options));
        var engine = cut.FindComponent<FormidableForm<Registration>>().Instance.Engine!;
        await cut.InvokeAsync(() => cut.Find("input[id$='-couponcode']").Change("WELCOME10"));
        cut.WaitForState(() => !engine.IsValidating, TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => { });
        return cut;
    }

    private static Dictionary<int, int> RenderCounts(IRenderedComponent<WorkoutShapedHost> cut) =>
        cut.FindComponents<IComponent>().ToDictionary(c => c.ComponentId, c => c.RenderCount);

    /// <summary>Counts the engine's publishes from the form's first render on, so a window can be measured in publishes; renders nothing.</summary>
    private sealed class PublishProbe : ComponentBase, IDisposable
    {
        private IFormidableEngine? _engine;

        [CascadingParameter]
        public FormidableFormContext? Context { get; set; }

        public int Publishes { get; private set; }

        protected override void OnParametersSet()
        {
            if (_engine is null && Context is not null)
            {
                _engine = Context.Engine;
                _engine.StateChanged += OnStateChanged;
            }
        }

        public void Dispose()
        {
            if (_engine is not null)
            {
                _engine.StateChanged -= OnStateChanged;
            }
        }

        private void OnStateChanged(object? sender, FormidableStateChangedEventArgs e) => Publishes++;
    }

    /// <summary>A leaf the form's fragment hands a fresh object each render, so its render count is the form's own; it observes nothing.</summary>
    private sealed class FormRenderCounter : ComponentBase
    {
        [Parameter]
        public object? Token { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddMarkupContent(0, "<i></i>");
    }

    /// <summary>A consumer's own component inside a FormidableField, handed the field context (an object, so every render of the wrapper re-sets it).</summary>
    private sealed class WrapperChild : ComponentBase
    {
        [Parameter]
        public FormidableFieldContext? Field { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddMarkupContent(0, "<span></span>");
    }

    /// <summary>Receives every ValueChanged, so no commit re-renders the host: it is not an IHandleEvent.</summary>
    private sealed class Sink
    {
        public void Set(Action<string?> write, string? value) => write(value);
    }

    /// <summary>The Workout page's form, component for component, without Virtualize (a fixed window of session rows stands in) and without the teaching panel.</summary>
    private sealed class WorkoutShapedHost : ComponentBase
    {
        private readonly Sink _sink = new();
        private readonly IModelValidator<Registration> _validator =
            new FluentValidationModelValidator<Registration>(new RegistrationValidator());

        [Parameter]
        public Registration Registration { get; set; } = default!;

        /// <summary>The form's options; the defaults unless a ceiling measures under a row-key check.</summary>
        [Parameter]
        public FormidableOptions FormOptions { get; set; } = Options;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<Registration>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<Registration>.Model), Registration);
            builder.AddComponentParameter(2, nameof(FormidableForm<Registration>.Options), FormOptions);
            builder.AddComponentParameter(3, nameof(FormidableForm<Registration>.Validator), _validator);
            builder.AddComponentParameter(4, nameof(FormidableForm<Registration>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                var seq = 0;
                inner.OpenComponent<PublishProbe>(seq++);
                inner.CloseComponent();
                inner.OpenComponent<FormRenderCounter>(seq++);
                inner.AddComponentParameter(seq++, nameof(FormRenderCounter.Token), new object());
                inner.CloseComponent();
                inner.OpenComponent<FormidableSummary>(seq++);
                inner.CloseComponent();

                Wrapped(inner, ref seq, () => Registration.ContactEmail, v => Registration.ContactEmail = v ?? "");
                Plain(inner, ref seq, () => Registration.EventName, v => Registration.EventName = v ?? "");
                Plain(inner, ref seq, () => Registration.EventDate, v => Registration.EventDate = v ?? "");
                Plain(inner, ref seq, () => Registration.EarlyBirdDeadline, v => Registration.EarlyBirdDeadline = v ?? "");
                Plain(inner, ref seq, () => Registration.Description, v => Registration.Description = v ?? "");
                Plain(inner, ref seq, () => Registration.CouponCode, v => Registration.CouponCode = v ?? "");

                inner.OpenComponent<FormidableField<bool>>(seq++);
                inner.AddComponentParameter(seq++, nameof(FormidableField<bool>.For), (Expression<Func<bool>>)(() => Registration.IncludeCatering));
                inner.AddComponentParameter(seq++, nameof(FormidableField<bool>.ChildContent), (RenderFragment<FormidableFieldContext>)(field => c =>
                {
                    c.OpenComponent<WrapperChild>(0);
                    c.AddComponentParameter(1, nameof(WrapperChild.Field), field);
                    c.CloseComponent();
                }));
                inner.CloseComponent();

                Plain(inner, ref seq, () => Registration.CateringHeadcount, v => Registration.CateringHeadcount = v ?? "");
                Plain(inner, ref seq, () => Registration.DietaryNotes, v => Registration.DietaryNotes = v ?? "");

                inner.OpenComponent<FormidableField<List<Attendee>>>(seq++);
                inner.AddComponentParameter(seq++, nameof(FormidableField<List<Attendee>>.For), (Expression<Func<List<Attendee>>>)(() => Registration.Attendees));
                inner.AddComponentParameter(seq++, nameof(FormidableField<List<Attendee>>.ChildContent), (RenderFragment<FormidableFieldContext>)(field => c =>
                {
                    var s = 0;
                    c.OpenComponent<WrapperChild>(s++);
                    c.AddComponentParameter(s++, nameof(WrapperChild.Field), field);
                    c.CloseComponent();
                    foreach (var attendee in Registration.Attendees)
                    {
                        c.OpenRegion(s++);
                        var t = 0;
                        Plain(c, ref t, () => attendee.Name, v => attendee.Name = v ?? "");
                        Plain(c, ref t, () => attendee.Email, v => attendee.Email = v ?? "");
                        c.CloseRegion();
                    }

                    c.OpenComponent<FormidableCollectionMessage<List<Attendee>>>(s++);
                    c.AddComponentParameter(s++, nameof(FormidableCollectionMessage<List<Attendee>>.For), (Expression<Func<List<Attendee>>>)(() => Registration.Attendees));
                    c.CloseComponent();
                }));
                inner.CloseComponent();

                foreach (var session in Registration.Sessions)
                {
                    inner.OpenRegion(seq++);
                    var t = 0;
                    inner.OpenComponent<FormidableInputText>(t++);
                    inner.AddComponentParameter(t++, nameof(FormidableInputText.For), (Expression<Func<string?>>)(() => session.Seats));
                    inner.AddComponentParameter(t++, "Value", session.Seats);
                    inner.AddComponentParameter(t++, "ValueChanged", Callback(v => session.Seats = v ?? ""));
                    inner.AddComponentParameter(t++, nameof(FormidableInputText.KeepRegistered), true);
                    inner.CloseComponent();
                    inner.OpenComponent<FormidableFieldMessage<string>>(t++);
                    inner.AddComponentParameter(t++, nameof(FormidableFieldMessage<string>.For), (Expression<Func<string>>)(() => session.Seats));
                    inner.CloseComponent();
                    inner.CloseRegion();
                }

                inner.OpenComponent<FormidableField<string>>(seq++);
                inner.AddComponentParameter(seq++, nameof(FormidableField<string>.For), (Expression<Func<string>>)(() => Registration.TicketTier));
                inner.AddComponentParameter(seq++, nameof(FormidableField<string>.ChildContent), (RenderFragment<FormidableFieldContext>)(field => c =>
                {
                    c.OpenComponent<WrapperChild>(0);
                    c.AddComponentParameter(1, nameof(WrapperChild.Field), field);
                    c.CloseComponent();
                    c.OpenComponent<FormidableRequiredIndicator<string>>(2);
                    c.AddComponentParameter(3, nameof(FormidableRequiredIndicator<string>.For), (Expression<Func<string>>)(() => Registration.TicketTier));
                    c.CloseComponent();
                    c.OpenComponent<FormidableFieldMessage<string>>(4);
                    c.AddComponentParameter(5, nameof(FormidableFieldMessage<string>.For), (Expression<Func<string>>)(() => Registration.TicketTier));
                    c.CloseComponent();
                }));
                inner.CloseComponent();

                inner.OpenComponent<FormidableRequiredIndicator<string>>(seq++);
                inner.AddComponentParameter(seq++, nameof(FormidableRequiredIndicator<string>.For), (Expression<Func<string>>)(() => Registration.VenueRegion));
                inner.CloseComponent();
                inner.OpenComponent<InputText>(seq++);
                inner.AddComponentParameter(seq++, nameof(InputText.Value), Registration.VenueRegion);
                inner.AddComponentParameter(seq++, nameof(InputText.ValueChanged), Callback(v => Registration.VenueRegion = v ?? ""));
                inner.AddComponentParameter(seq++, nameof(InputText.ValueExpression), (Expression<Func<string?>>)(() => Registration.VenueRegion));
                inner.CloseComponent();
                inner.OpenComponent<FormidableFieldAnchor<string>>(seq++);
                inner.AddComponentParameter(seq++, nameof(FormidableFieldAnchor<string>.For), (Expression<Func<string>>)(() => Registration.VenueRegion));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }

        private EventCallback<string?> Callback(Action<string?> write) =>
            EventCallback.Factory.Create<string?>(_sink, v => _sink.Set(write, v));

        private void Plain(RenderTreeBuilder b, ref int seq, Expression<Func<string>> accessor, Action<string?> setter)
        {
            var value = accessor.Compile()();
            b.OpenComponent<FormidableRequiredIndicator<string>>(seq++);
            b.AddComponentParameter(seq++, nameof(FormidableRequiredIndicator<string>.For), accessor);
            b.CloseComponent();
            b.OpenComponent<FormidableInputText>(seq++);
            b.AddComponentParameter(seq++, nameof(FormidableInputText.For), Expression.Lambda<Func<string?>>(accessor.Body));
            b.AddComponentParameter(seq++, "Value", value);
            b.AddComponentParameter(seq++, "ValueChanged", Callback(setter));
            b.CloseComponent();
            b.OpenComponent<FormidableFieldMessage<string>>(seq++);
            b.AddComponentParameter(seq++, nameof(FormidableFieldMessage<string>.For), accessor);
            b.CloseComponent();
        }

        private void Wrapped(RenderTreeBuilder b, ref int seq, Expression<Func<string>> accessor, Action<string?> setter)
        {
            b.OpenComponent<FormidableField<string>>(seq++);
            b.AddComponentParameter(seq++, nameof(FormidableField<string>.For), accessor);
            b.AddComponentParameter(seq++, nameof(FormidableField<string>.ChildContent), (RenderFragment<FormidableFieldContext>)(field => c =>
            {
                var s = 0;
                c.OpenComponent<WrapperChild>(s++);
                c.AddComponentParameter(s++, nameof(WrapperChild.Field), field);
                c.CloseComponent();
                Plain(c, ref s, accessor, setter);
            }));
            b.CloseComponent();
        }
    }

    /// <summary>The sample's attendee row, field for field.</summary>
    private sealed class Attendee
    {
        public string Name { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;
    }

    /// <summary>The sample's session row, field for field.</summary>
    private sealed class SessionPick
    {
        public int Id { get; init; }

        public string Title { get; init; } = string.Empty;

        public string Seats { get; set; } = "0";
    }

    /// <summary>The sample's EventRegistration, field for field.</summary>
    private sealed class Registration
    {
        public string ContactEmail { get; set; } = string.Empty;

        public string EventName { get; set; } = string.Empty;

        public string EventDate { get; set; } = string.Empty;

        public string EarlyBirdDeadline { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string CouponCode { get; set; } = string.Empty;

        public bool IncludeCatering { get; set; }

        public string CateringHeadcount { get; set; } = string.Empty;

        public string DietaryNotes { get; set; } = string.Empty;

        public string TicketTier { get; set; } = string.Empty;

        public string VenueRegion { get; set; } = string.Empty;

        public List<Attendee> Attendees { get; set; } = [];

        public List<SessionPick> Sessions { get; set; } = [];
    }

    /// <summary>
    /// The sample's EventRegistrationValidator with the same rules on the same fields, the memoised
    /// async email rule folded into the plain EmailAddress rule: the ceilings are about renders,
    /// not about an async pass.
    /// </summary>
    private sealed class RegistrationValidator : DraftSubmitValidator<Registration>
    {
        private static bool TryParseDate(string value, out DateTime date) =>
            DateTime.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date)
            && date.Year is >= 1900 and <= 2100;

        protected override void ConfigureDraftRules()
        {
            RuleFor(r => r.ContactEmail)
                .EmailAddress()
                .WithMessage("Contact email must be a valid email address")
                .When(r => !string.IsNullOrEmpty(r.ContactEmail));

            RuleFor(r => r.EventDate)
                .Must(d => TryParseDate(d, out _))
                .WithMessage("Event date must be a valid date")
                .When(r => !string.IsNullOrEmpty(r.EventDate));

            RuleFor(r => r.EarlyBirdDeadline)
                .Must(d => TryParseDate(d, out _))
                .WithMessage("Early-bird deadline must be a valid date")
                .When(r => !string.IsNullOrEmpty(r.EarlyBirdDeadline));

            RuleFor(r => r.EarlyBirdDeadline)
                .Must((r, deadline) =>
                {
                    TryParseDate(deadline, out var deadlineDate);
                    TryParseDate(r.EventDate, out var eventDate);
                    return deadlineDate <= eventDate;
                })
                .WithMessage("Early-bird deadline must be on or before the event date")
                .When(r => TryParseDate(r.EventDate, out _) && TryParseDate(r.EarlyBirdDeadline, out _));

            RuleFor(r => r.CateringHeadcount)
                .Must(v => int.TryParse(v, out var n) && n is >= 1 and <= 500)
                .WithMessage("Catering headcount must be a whole number between 1 and 500")
                .When(r => r.IncludeCatering && !string.IsNullOrEmpty(r.CateringHeadcount));

            RuleForEach(r => r.Sessions).ChildRules(session =>
                session.RuleFor(s => s.Seats)
                    .Must(v => int.TryParse(v, out var n) && n is >= 0 and <= 500)
                    .WithMessage("Seats must be a whole number between 0 and 500"));

            RuleFor(r => r.Attendees)
                .Must(a => a.Count <= 10)
                .WithSeverity(Severity.Warning)
                .WithMessage("More than 10 attendees needs approval; submission is not blocked");

            RuleFor(r => r.Attendees)
                .Must(a => a.Count > 0)
                .WithSeverity(Severity.Info)
                .WithMessage("You can add attendees now or after registering");
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(r => r.ContactEmail).NotEmpty().WithMessage("Contact email is required");
            RuleFor(r => r.EventName).NotEmpty().WithMessage("Event name is required");
            RuleFor(r => r.EventDate).NotEmpty().WithMessage("Event date is required");
            RuleFor(r => r.TicketTier).NotEmpty().WithMessage("Ticket tier is required");
            RuleFor(r => r.VenueRegion).NotEmpty().WithMessage("Venue region is required");
            RuleFor(r => r.DietaryNotes).NotEmpty().WithMessage("Dietary notes are required");

            RuleForEach(r => r.Attendees).ChildRules(attendee =>
            {
                attendee.RuleFor(a => a.Name).NotEmpty().WithMessage("Attendee name is required");

                attendee.RuleFor(a => a.Email)
                    .EmailAddress()
                    .WithMessage("Attendee email must be a valid email address")
                    .When(a => !string.IsNullOrEmpty(a.Email));
            });
        }
    }
}
