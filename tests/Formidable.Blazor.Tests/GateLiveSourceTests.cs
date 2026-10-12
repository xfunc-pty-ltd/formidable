using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// Which live errors take the form-level message (the gate) down after a blocked submit. A live
/// error explains the block only when it is on a field the page renders (the model-level field, or
/// one a registering component such as a kit input or a
/// <see cref="FormidableFieldAnchor{TValue}"/> renders), or when
/// <see cref="FormidableOptions.DisclosureOverride"/> answers <see langword="true"/> for it. A
/// native input with no anchor registers nothing, so its live error leaves the gate standing,
/// whether or not the input is on screen.
/// </summary>
/// <remarks>
/// Code is required and renders nothing unless a test says otherwise, so a submit with it empty is
/// blocked by a field nothing shows. Email has a native <see cref="InputText"/> with Blazor's own
/// <see cref="ValidationMessage{TValue}"/>, anchored or not. The clock is a
/// <see cref="FakeTimeProvider"/>: the whole-form re-check after an edit runs only when a test
/// advances it.
/// </remarks>
public class GateLiveSourceTests : BunitContext
{
    private const string NameRequired = "Name is required";
    private const string CodeRequired = "Code is required";
    private const string CodeWrong = "Code looks wrong";
    private const string EmailWrong = "Email looks wrong";

    private static readonly string GateText = new FormidableOptions().DefensiveGateMessage;

    private readonly FakeTimeProvider _clock = new();

    public GateLiveSourceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddFormidableBlazor();
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddSingleton<IValidator<Account>>(new AccountValidator());
        Services.AddSingleton<IFormidableFocusService>(new NoFocus());
    }

    /// <summary>The native input for Email: absent, without an anchor, or with one.</summary>
    public enum EmailInput
    {
        None,
        Unanchored,
        Anchored,
    }

    /// <summary>What renders Code: nothing, a lone <see cref="FormidableFieldMessage{TValue}"/>, that message with a <see cref="FormidableFieldAnchor{TValue}"/>, or a kit input with its message.</summary>
    public enum CodeView
    {
        None,
        Message,
        MessageAndAnchor,
        Input,
    }

    // The case the change exists for. On a form with no summary, Email's native input is made
    // invalid, then hidden, then a submit is blocked. An edit to Name re-checks every field the
    // visitor changed, Email included, and Email's error comes back with nothing on screen to show
    // it. The gate stays, after the edit and after the whole-form re-check. Mutation that must
    // break it: let every live error count (drop the field test from GateActive's live loop), and
    // the gate goes at the edit.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_hidden_unanchored_native_inputs_live_error_keeps_the_gate(bool attach)
    {
        var account = new Account();
        var cut = RenderHost(account, attach, email: EmailInput.Unanchored);

        await EditAsync(cut, "Email", "bad");
        Assert.Equal([EmailWrong], NativeMessages(cut));

        await ShowEmailAsync(cut, false);
        Assert.False((await SubmitAsync(cut)).CanProceed);
        Assert.Equal([GateText], ModelMessages(cut, account));

        await EditAsync(cut, "Name", "Bea");

        Assert.Equal([GateText], ModelMessages(cut, account));
        AssertGateOnEverySurface(cut, account);
        var email = FieldIdentifier.Create(() => account.Email);
        Assert.Contains(Engine(cut).GetVisibleIssues(), v => v.Field.Equals(email) && v.Issue.Message == EmailWrong);

        await RecheckAsync(cut);

        Assert.Equal([GateText], ModelMessages(cut, account));
        AssertGateOnEverySurface(cut, account);

        await Services.DisposeAsync();
    }

    // A kit-only page behaves the same with an override answering false for Name and without
    // one. A blocked submit for hidden Code shows the gate; emptying Name shows "Name is
    // required" at Name and takes the gate down. The override does not hide Name's live message
    // (under the default policy the live channel shows an engaged field's errors whatever an
    // override says), so the gate must not ask it either. Mutation that must break the override
    // case: ask IsVisible in GateActive's live loop, which answers the override's false, and the
    // gate stays beside Name's message.
    [Theory]
    [InlineData("plain")]
    [InlineData("override")]
    public async Task Kit_only_page_matches_2_0_0_with_and_without_an_override(string variant)
    {
        var options = new FormidableOptions();
        if (variant == "override")
        {
            options.DisclosureOverride = issue => issue.Path == nameof(Account.Name) ? false : null;
        }

        var account = new Account { Code = string.Empty };
        var cut = RenderHost(account, options: options);

        Assert.False((await SubmitAsync(cut)).CanProceed);
        Assert.Equal([GateText], ModelMessages(cut, account));
        Assert.Empty(KitMessages(cut, "Name"));

        await EditAsync(cut, "Name", string.Empty);

        Assert.Equal([NameRequired], KitMessages(cut, "Name"));
        Assert.Empty(ModelMessages(cut, account));

        await RecheckAsync(cut);

        Assert.Equal([NameRequired], KitMessages(cut, "Name"));
        Assert.Empty(ModelMessages(cut, account));

        await Services.DisposeAsync();
    }

    // The side effect: a native input with no anchor that stays on screen. A blocked submit shows
    // the gate and takes Email's message off. The next edit brings Email's message back, and the
    // gate stays beside it, because nothing registers Email. A summary lists both.
    // Mutation that must break it: let every live error count, and the gate goes at the edit.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_visible_unanchored_native_input_shows_the_gate_beside_its_message_after_an_edit(bool summary)
    {
        var account = new Account();
        var cut = RenderHost(account, summary: summary, email: EmailInput.Unanchored);

        await EditAsync(cut, "Email", "bad");
        Assert.Equal([EmailWrong], NativeMessages(cut));
        Assert.Empty(ModelMessages(cut, account));

        Assert.False((await SubmitAsync(cut)).CanProceed);
        Assert.Empty(NativeMessages(cut));
        Assert.Equal([GateText], ModelMessages(cut, account));

        await EditAsync(cut, "Name", "Bea");

        Assert.Equal([EmailWrong], NativeMessages(cut));
        Assert.Equal([GateText], ModelMessages(cut, account));
        if (summary)
        {
            var entries = SummaryEntries(cut);
            Assert.Equal(2, entries.Count);
            Assert.Contains(GateText, entries);
            Assert.Contains(EmailWrong, entries);
        }

        await RecheckAsync(cut);

        Assert.Equal([EmailWrong], NativeMessages(cut));
        Assert.Equal([GateText], ModelMessages(cut, account));

        await Services.DisposeAsync();
    }

    // Control: a native input with an anchor. On screen, a blocked submit shows its error and no
    // gate, and an edit elsewhere keeps it that way. Hidden, it has left the page, so
    // its error stops counting the moment it goes: the gate shows at the submit and stays through
    // the edit and the re-check.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_anchored_native_input_keeps_the_gate_it_had_at_2_0_0(bool shown)
    {
        var account = new Account();
        var cut = RenderHost(account, email: EmailInput.Anchored);

        await EditAsync(cut, "Email", "bad");
        Assert.Equal([EmailWrong], NativeMessages(cut));
        if (!shown)
        {
            await ShowEmailAsync(cut, false);
        }

        Assert.False((await SubmitAsync(cut)).CanProceed);
        AssertAnchoredOutcome(cut, account, shown);

        await EditAsync(cut, "Name", "Bea");
        AssertAnchoredOutcome(cut, account, shown);

        await RecheckAsync(cut);
        AssertAnchoredOutcome(cut, account, shown);

        await Services.DisposeAsync();
    }

    // The summary keeps listing a hidden unanchored input's live error, so the block is not
    // silent there, and the gate's line shows beside it: a summary's listing does not count as
    // showing a field's error, the same answer the gate gives a hidden field a submit revealed.
    // Mutation that must break it: let every live error count, and the summary lists Email's
    // error alone.
    [Fact]
    public async Task A_summary_lists_a_hidden_unanchored_native_inputs_live_error_beside_the_gate()
    {
        var account = new Account();
        var cut = RenderHost(account, summary: true, email: EmailInput.Unanchored);

        await EditAsync(cut, "Email", "bad");
        await ShowEmailAsync(cut, false);
        Assert.False((await SubmitAsync(cut)).CanProceed);
        Assert.Equal([GateText], SummaryEntries(cut));

        await EditAsync(cut, "Name", "Bea");

        var entries = SummaryEntries(cut);
        Assert.Equal(2, entries.Count);
        Assert.Contains(EmailWrong, entries);
        Assert.Contains(GateText, entries);

        await Services.DisposeAsync();
    }

    // An override that answers true for an issue counts it as shown, wherever its field is. Email's
    // native input has no anchor and is valid at a submit blocked by hidden Code, so the gate
    // shows. Making Email invalid then brings its live error, which the override answers true for,
    // and the gate goes, as it would for a registered field. Mutation that must break it: drop the
    // override clause from GateActive's live loop, and the gate stays beside Email's message.
    [Fact]
    public async Task An_override_answering_true_lets_an_unregistered_fields_live_error_take_the_gate_down()
    {
        var options = new FormidableOptions
        {
            DisclosureOverride = issue => issue.Path == nameof(Account.Email) ? true : null,
        };
        var account = new Account { Code = string.Empty };
        var cut = RenderHost(account, email: EmailInput.Unanchored, options: options);

        Assert.False((await SubmitAsync(cut)).CanProceed);
        Assert.Equal([GateText], ModelMessages(cut, account));

        await EditAsync(cut, "Email", "bad");

        Assert.Equal([EmailWrong], NativeMessages(cut));
        Assert.Empty(ModelMessages(cut, account));

        await RecheckAsync(cut);

        Assert.Equal([EmailWrong], NativeMessages(cut));
        Assert.Empty(ModelMessages(cut, account));

        await Services.DisposeAsync();
    }

    // The anchor's promise. Email's native input is on screen and valid, and a submit blocked by
    // Code, which renders nothing, shows the gate. Making Email invalid brings its live error: with
    // an anchor it takes the gate down, as a kit input's would; without one the gate stays beside
    // it. Mutation that must break the anchored case: count a live error only where the override
    // answers true (drop IsRendered from GateActive's live loop).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_anchored_native_inputs_live_error_takes_down_a_gate_raised_for_another_field(bool anchored)
    {
        var account = new Account { Code = string.Empty };
        var cut = RenderHost(account, email: anchored ? EmailInput.Anchored : EmailInput.Unanchored);

        Assert.False((await SubmitAsync(cut)).CanProceed);
        Assert.Equal([GateText], ModelMessages(cut, account));

        await EditAsync(cut, "Email", "bad");

        Assert.Equal([EmailWrong], NativeMessages(cut));
        Assert.Equal(anchored ? Array.Empty<string>() : [GateText], ModelMessages(cut, account));

        await Services.DisposeAsync();
    }

    // A FormidableFieldMessage registers nothing, so a live error it shows leaves the gate beside
    // it, and an anchor beside the message is what makes the error count. Email is invalid and
    // renders nothing, so a submit shows the gate; then the page's code sets Code to a bad value
    // and tells the form, which shows "Code looks wrong" at Code's message either way. Mutation
    // that must break the anchored case: drop IsRendered from GateActive's live loop. Mutation
    // that must break the other: let every live error count.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_field_messages_live_error_takes_the_gate_down_only_with_an_anchor(bool anchored)
    {
        var account = new Account { Email = "bad" };
        var cut = RenderHost(account, code: anchored ? CodeView.MessageAndAnchor : CodeView.Message);

        Assert.False((await SubmitAsync(cut)).CanProceed);
        Assert.Equal([GateText], ModelMessages(cut, account));

        await cut.InvokeAsync(() =>
        {
            account.Code = "x";
            Engine(cut).EditContext.NotifyFieldChanged(FieldIdentifier.Create(() => account.Code));
        });
        await SettleAsync(cut);

        Assert.Equal([CodeWrong], KitMessages(cut, "Code"));
        Assert.Equal(anchored ? Array.Empty<string>() : [GateText], ModelMessages(cut, account));

        await Services.DisposeAsync();
    }

    // A field that starts registering while its live error stands takes the gate down at once, on
    // every surface, with no re-check behind it: the clock never moves here, and one case turns the
    // re-check off. Email's native input has no anchor; after a blocked submit and an edit the gate
    // stands beside Email's message, then an anchor for Email mounts. Each case also runs in attach
    // mode, whose root hears the move from a post of its own rather than from its own render.
    // Mutation that must break it: drop the stored-gate clause from OnRenderedFieldsChanged's
    // republish condition. The store then keeps the gate, and the model-level list and a summary,
    // which nothing re-renders, keep showing it until the next pass, under either root.
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task An_anchor_arriving_for_a_fields_live_error_takes_the_gate_down_at_once(bool summary, bool noRecheck, bool attach)
    {
        var options = new FormidableOptions();
        if (noRecheck)
        {
            options.RefreshDebounce = Timeout.InfiniteTimeSpan;
        }

        var account = new Account();
        var cut = RenderHost(account, attach: attach, summary: summary, email: EmailInput.Unanchored, options: options);
        await EditAsync(cut, "Email", "bad");
        Assert.False((await SubmitAsync(cut)).CanProceed);
        await EditAsync(cut, "Name", "Bea");
        Assert.Equal([GateText], ModelMessages(cut, account));

        await cut.InvokeAsync(() => cut.Render(parameters => parameters.Add(p => p.Email, EmailInput.Anchored)));
        await SettleAsync(cut);

        AssertNoGateAnywhere(cut, account);
        Assert.Equal([EmailWrong], NativeMessages(cut));
        if (summary)
        {
            Assert.Equal([EmailWrong], SummaryEntries(cut));
        }

        await Services.DisposeAsync();
    }

    // The same move on a page with no raw control. A load of values engages Code, which renders
    // nothing, and its bad value fails; a submit shows the gate, and an edit to Name brings Code's
    // live error back with the gate beside it. A kit input for Code then mounts, and the gate goes
    // at once. Mutation that must break it: drop the stored-gate clause from
    // OnRenderedFieldsChanged's republish condition.
    [Fact]
    public async Task A_kit_input_mounting_for_a_loaded_fields_live_error_takes_the_gate_down_at_once()
    {
        var account = new Account { Code = "x" };
        var cut = RenderHost(account);
        await cut.InvokeAsync(() => Engine(cut).DiscloseLoadedValuesAsync());
        await SettleAsync(cut);
        Assert.False((await SubmitAsync(cut)).CanProceed);
        await EditAsync(cut, "Name", "Bea");
        Assert.Equal([GateText], ModelMessages(cut, account));

        await cut.InvokeAsync(() => cut.Render(parameters => parameters.Add(p => p.Code, CodeView.Input)));
        await SettleAsync(cut);

        AssertNoGateAnywhere(cut, account);
        Assert.Equal([CodeWrong], KitMessages(cut, "Code"));

        await Services.DisposeAsync();
    }

    // Control on cost: a field moving on and off the page republishes nothing for the gate unless
    // the move changes it. A blocked submit for Code shows the gate, emptying Name takes it down,
    // and then the Note input mounts and unmounts four times, under either root. Mutation that must
    // break it: rebuild on every move while an armed gate's submit answer and any live error stand,
    // and each of the eight moves publishes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fields_moving_after_a_kit_error_took_the_gate_down_publish_nothing(bool attach)
    {
        var account = new Account { Code = string.Empty };
        var cut = RenderHost(account, attach: attach, note: true);
        Assert.False((await SubmitAsync(cut)).CanProceed);
        await EditAsync(cut, "Name", string.Empty);
        Assert.Empty(ModelMessages(cut, account));
        var published = 0;
        Engine(cut).EditContext.OnValidationStateChanged += (_, _) => published++;

        for (var move = 0; move < 4; move++)
        {
            await ShowNoteAsync(cut, false);
            await ShowNoteAsync(cut, true);
        }

        Assert.Equal(0, published);
        Assert.Equal([NameRequired], KitMessages(cut, "Name"));
        Assert.Empty(ModelMessages(cut, account));

        await Services.DisposeAsync();
    }

    private static void AssertNoGateAnywhere(IRenderedComponent<AccountHost> cut, Account account)
    {
        var engine = Engine(cut);
        var modelLevel = new FieldIdentifier(account, string.Empty);
        Assert.Empty(ModelMessages(cut, account));
        Assert.Empty(engine.GetIssues(modelLevel));
        Assert.Empty(engine.EditContext.GetValidationMessages(modelLevel));
        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Field.Equals(modelLevel));
    }

    private static void AssertAnchoredOutcome(IRenderedComponent<AccountHost> cut, Account account, bool shown)
    {
        if (shown)
        {
            Assert.Equal([EmailWrong], NativeMessages(cut));
            Assert.Empty(ModelMessages(cut, account));
        }
        else
        {
            Assert.Equal([GateText], ModelMessages(cut, account));
        }
    }

    private static void AssertGateOnEverySurface(IRenderedComponent<AccountHost> cut, Account account)
    {
        var engine = Engine(cut);
        var modelLevel = new FieldIdentifier(account, string.Empty);
        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message == GateText);
        Assert.Contains(engine.GetVisibleIssues(), v => v.Field.Equals(modelLevel) && v.Issue.Message == GateText);
        Assert.Contains(engine.EditContext.GetValidationMessages(modelLevel), m => m == GateText);
    }

    private IRenderedComponent<AccountHost> RenderHost(
        Account account,
        bool attach = false,
        bool summary = false,
        EmailInput email = EmailInput.None,
        FormidableOptions? options = null,
        CodeView code = CodeView.None,
        bool note = false) =>
        Render<AccountHost>(parameters =>
        {
            parameters
                .Add(p => p.Account, account)
                .Add(p => p.Attach, attach)
                .Add(p => p.Summary, summary)
                .Add(p => p.Email, email)
                .Add(p => p.Code, code)
                .Add(p => p.ShowNote, note);
            if (options is not null)
            {
                parameters.Add(p => p.Options, options);
            }
        });

    // Waits for any check the step started, then lets a reconcile or a posted republish run: the
    // dispatcher runs one piece of work at a time, so the queued no-op completes after them.
    private static async Task SettleAsync(IRenderedComponent<AccountHost> cut)
    {
        cut.WaitForState(() => !Engine(cut).IsValidating);
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => { });
    }

    private static async Task EditAsync(IRenderedComponent<AccountHost> cut, string entry, string value)
    {
        await cut.InvokeAsync(() => cut.Find($"div[data-entry={entry}] input").Change(value));
        await SettleAsync(cut);
    }

    private static async Task ShowEmailAsync(IRenderedComponent<AccountHost> cut, bool show)
    {
        await cut.InvokeAsync(() => cut.Render(parameters => parameters.Add(p => p.ShowEmail, show)));
        await SettleAsync(cut);
    }

    private static async Task ShowNoteAsync(IRenderedComponent<AccountHost> cut, bool show)
    {
        await cut.InvokeAsync(() => cut.Render(parameters => parameters.Add(p => p.ShowNote, show)));
        await SettleAsync(cut);
    }

    private async Task RecheckAsync(IRenderedComponent<AccountHost> cut)
    {
        await cut.InvokeAsync(() => _clock.Advance(TimeSpan.FromMilliseconds(301)));
        await SettleAsync(cut);
    }

    private static async Task<SubmitOutcome> SubmitAsync(IRenderedComponent<AccountHost> cut)
    {
        var outcome = cut.Instance.Attach
            ? await cut.InvokeAsync(() => cut.FindComponent<FormidableValidator<Account>>().Instance.ValidateForSubmitAsync())
            : await cut.InvokeAsync(() => cut.FindComponent<FormidableForm<Account>>().Instance.SubmitAsync());
        await SettleAsync(cut);
        return outcome;
    }

    private static IFormidableEngine Engine(IRenderedComponent<AccountHost> cut) =>
        cut.Instance.Attach
            ? cut.FindComponent<FormidableValidator<Account>>().Instance.Engine!
            : cut.FindComponent<FormidableForm<Account>>().Instance.Engine!;

    private static List<string> ModelMessages(IRenderedComponent<AccountHost> cut, Account account)
    {
        var id = FormidableFieldId.MessagesFor(new FieldIdentifier(account, string.Empty));
        var list = Assert.Single(cut.FindAll("ul"), ul => ul.Id == id);
        return list.Children.Select(li => li.TextContent.Trim()).ToList();
    }

    private static List<string> KitMessages(IRenderedComponent<AccountHost> cut, string entry) =>
        cut.FindAll($"div[data-entry={entry}] li").Select(li => li.TextContent.Trim()).ToList();

    private static List<string> NativeMessages(IRenderedComponent<AccountHost> cut) =>
        cut.FindAll("div[data-entry=Email] .validation-message").Select(m => m.TextContent.Trim()).ToList();

    private static List<string> SummaryEntries(IRenderedComponent<AccountHost> cut) =>
        cut.FindAll("li.formidable-summary__item").Select(li => li.TextContent.Trim()).ToList();

    /// <summary>A name, a code and an email, each valid until a test empties or breaks it.</summary>
    public sealed class Account
    {
        public string? Name { get; set; } = "Ada";

        public string? Code { get; set; } = "A1";

        public string? Email { get; set; } = "ada@example.com";

        public string? Note { get; set; } = "note";
    }

    private sealed class AccountValidator : AbstractValidator<Account>
    {
        public AccountValidator()
        {
            RuleFor(m => m.Name).NotEmpty().WithMessage(NameRequired);
            RuleFor(m => m.Code).NotEmpty().WithMessage(CodeRequired);
            RuleFor(m => m.Code).Matches("^[A-Z][0-9]$").When(m => !string.IsNullOrEmpty(m.Code)).WithMessage(CodeWrong);
            RuleFor(m => m.Email).EmailAddress().WithMessage(EmailWrong);
        }
    }

    /// <summary>Takes no focus, so a blocked submit's focus move touches no script.</summary>
    private sealed class NoFocus : IFormidableFocusService
    {
        public ValueTask<bool> FocusAsync(FieldIdentifier field) => ValueTask.FromResult(false);
    }

    /// <summary>A form over <see cref="Account"/>, on either root: a model-level list, an optional summary, a kit input for Name, an optional native input for Email that can hide, Code as <see cref="CodeView"/> says, and an optional kit input for Note.</summary>
    private sealed class AccountHost : ComponentBase
    {
        [Parameter]
        public Account Account { get; set; } = default!;

        [Parameter]
        public bool Attach { get; set; }

        [Parameter]
        public bool Summary { get; set; }

        [Parameter]
        public EmailInput Email { get; set; }

        [Parameter]
        public bool ShowEmail { get; set; } = true;

        [Parameter]
        public CodeView Code { get; set; }

        [Parameter]
        public bool ShowNote { get; set; }

        [Parameter]
        public FormidableOptions? Options { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Attach)
            {
                builder.OpenComponent<EditForm>(0);
                builder.AddComponentParameter(1, nameof(EditForm.Model), Account);
                builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableValidator<Account>>(0);
                    if (Options is not null)
                    {
                        inner.AddComponentParameter(1, nameof(FormidableValidator<Account>.Options), Options);
                    }

                    inner.AddComponentParameter(2, nameof(FormidableValidator<Account>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Content));
                    inner.CloseComponent();
                }));
                builder.CloseComponent();
                return;
            }

            builder.OpenComponent<FormidableForm<Account>>(10);
            builder.AddComponentParameter(11, nameof(FormidableForm<Account>.Model), Account);
            if (Options is not null)
            {
                builder.AddComponentParameter(12, nameof(FormidableForm<Account>.Options), Options);
            }

            builder.AddComponentParameter(13, nameof(FormidableForm<Account>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Content));
            builder.CloseComponent();
        }

        private void Content(RenderTreeBuilder builder)
        {
            if (Summary)
            {
                builder.OpenComponent<FormidableSummary>(0);
                builder.CloseComponent();
            }

            builder.OpenComponent<FormidableModelMessage>(1);
            builder.CloseComponent();

            Expression<Func<string?>> name = () => Account.Name;
            builder.OpenElement(10, "div");
            builder.AddAttribute(11, "data-entry", "Name");
            builder.OpenComponent<FormidableInputText>(12);
            builder.AddComponentParameter(13, "For", name);
            builder.AddComponentParameter(14, "Value", Account.Name);
            builder.AddComponentParameter(15, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => Account.Name = v));
            builder.CloseComponent();
            builder.OpenComponent<FormidableFieldMessage<string?>>(16);
            builder.AddComponentParameter(17, "For", name);
            builder.CloseComponent();
            builder.CloseElement();

            if (Email != EmailInput.None && ShowEmail)
            {
                Expression<Func<string?>> email = () => Account.Email;
                builder.OpenElement(30, "div");
                builder.AddAttribute(31, "data-entry", "Email");
                builder.OpenComponent<InputText>(32);
                builder.AddComponentParameter(33, "Value", Account.Email);
                builder.AddComponentParameter(34, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => Account.Email = v));
                builder.AddComponentParameter(35, "ValueExpression", email);
                builder.CloseComponent();
                builder.OpenComponent<ValidationMessage<string?>>(36);
                builder.AddComponentParameter(37, "For", email);
                builder.CloseComponent();
                if (Email == EmailInput.Anchored)
                {
                    builder.OpenComponent<FormidableFieldAnchor<string?>>(38);
                    builder.AddComponentParameter(39, "For", email);
                    builder.CloseComponent();
                }

                builder.CloseElement();
            }

            if (Code != CodeView.None)
            {
                Expression<Func<string?>> code = () => Account.Code;
                builder.OpenElement(50, "div");
                builder.AddAttribute(51, "data-entry", "Code");
                if (Code == CodeView.Input)
                {
                    builder.OpenComponent<FormidableInputText>(52);
                    builder.AddComponentParameter(53, "For", code);
                    builder.AddComponentParameter(54, "Value", Account.Code);
                    builder.AddComponentParameter(55, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => Account.Code = v));
                    builder.CloseComponent();
                }

                builder.OpenComponent<FormidableFieldMessage<string?>>(56);
                builder.AddComponentParameter(57, "For", code);
                builder.CloseComponent();
                if (Code == CodeView.MessageAndAnchor)
                {
                    builder.OpenComponent<FormidableFieldAnchor<string?>>(58);
                    builder.AddComponentParameter(59, "For", code);
                    builder.CloseComponent();
                }

                builder.CloseElement();
            }

            if (ShowNote)
            {
                Expression<Func<string?>> note = () => Account.Note;
                builder.OpenElement(70, "div");
                builder.AddAttribute(71, "data-entry", "Note");
                builder.OpenComponent<FormidableInputText>(72);
                builder.AddComponentParameter(73, "For", note);
                builder.AddComponentParameter(74, "Value", Account.Note);
                builder.AddComponentParameter(75, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => Account.Note = v));
                builder.CloseComponent();
                builder.CloseElement();
            }
        }
    }
}
