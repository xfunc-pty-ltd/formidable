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
/// The form-level message (the gate) after a server reply. A reply whose errors all name fields
/// that are off screen shows the gate, as a blocked submit with nothing on screen does, and the
/// reply's focus move lands on the form. A server error on a field on screen explains the block
/// itself, so the gate gives way to it. Whatever raised the gate, only a submit disarms it, though a
/// re-check that replaces the reply takes it down when the form's own answer finds no error.
/// </summary>
/// <remarks>
/// Name always renders a kit input. Code renders one only while a test shows it, so a reply naming
/// Code with Code hidden names nothing on screen. Note's kit input mounts only where a test moves
/// a field that has nothing to do with the gate. The clock is a <see cref="FakeTimeProvider"/>:
/// the whole-form re-check after an edit or a move runs only when a test advances it, so what a
/// test sees after a move is what the move itself did, and the rule counter shows that no rule ran
/// for it. Focus requests are recorded, and the field order is the document's, with the form first,
/// as a browser reports it.
/// </remarks>
public class GateServerReplyTests : BunitContext
{
    private const string NameTaken = "Name is taken";
    private const string CodeRequired = "Code is required";
    private const string CodeTaken = "Code is taken";
    private const string OrderRejected = "This account cannot be saved";

    private static readonly string GateText = new FormidableOptions().DefensiveGateMessage;

    private readonly FakeTimeProvider _clock = new();
    private readonly AccountValidator _rules = new();
    private readonly RecordingFocus _focus = new();

    public GateServerReplyTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddFormidableBlazor();
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddSingleton<IValidator<Account>>(_rules);
        Services.AddSingleton<IFormidableFocusService>(_focus);
        Services.AddSingleton<IFormidableFieldOrderService>(new DocumentOrder());
    }

    // The case the change exists for. A client submit passes, then the server rejects Code, which
    // the page does not render, on a form with no summary. Nothing on screen says why the save
    // failed, so the gate shows on every surface, and the reply's focus move asks for the form.
    // Attach mode moves no focus on a reply. Mutation that must break it: drop the arming from
    // ApplyServerIssues, and nothing shows at all.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reply_naming_only_hidden_fields_shows_the_gate(bool attach)
    {
        var account = new Account();
        var cut = RenderHost(account, attach);
        Assert.True((await SubmitAsync(cut)).CanProceed);
        var asked = _focus.Requests.Count;

        await ApplyAsync(cut, new ValidationIssue("Code", CodeTaken));

        Assert.Equal([GateText], ModelMessages(cut, account));
        AssertGateOnEverySurface(cut, account);
        Assert.Equal([CodeTaken], Messages(Engine(cut).GetIssues(Code(account))));
        var moves = _focus.Requests.Skip(asked).ToList();
        if (attach)
        {
            Assert.Empty(moves);
        }
        else
        {
            Assert.Equal([new FieldIdentifier(account, string.Empty)], moves);
        }

        await Services.DisposeAsync();
    }

    // Showing the field brings its message back and takes the gate down at once, with no re-check
    // behind it and no rule run: the error is on screen now and explains the block itself. A
    // summary lists the error alone. Mutation that must break it: drop the server-error test from
    // GateActive, and the gate stays beside Code's message.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Showing_the_field_restores_its_message_and_drops_the_gate(bool summary)
    {
        var account = new Account();
        var cut = RenderHost(account, summary: summary);
        Assert.True((await SubmitAsync(cut)).CanProceed);
        await ApplyAsync(cut, new ValidationIssue("Code", CodeTaken));
        Assert.Equal([GateText], ModelMessages(cut, account));
        var runs = _rules.Runs;

        await ShowCodeAsync(cut, true);

        Assert.Equal([CodeTaken], KitMessages(cut, "Code"));
        AssertNoGateAnywhere(cut, account);
        if (summary)
        {
            Assert.Equal([CodeTaken], SummaryEntries(cut));
        }

        Assert.Equal(runs, _rules.Runs);

        await Services.DisposeAsync();
    }

    // Hiding the field again takes its message off the page, and the gate comes back at once, with
    // no re-check behind it and no rule run. Mutation that must break it: leave standing server
    // errors out of GateTurnsOnVisibility, and nothing republishes at the move. Each root catches
    // that on a different surface. Under FormidableForm the form-level list still shows the gate,
    // because the move changes the field order and the new order's notification re-renders the
    // list, so the edit context's messages are what keep the answer from before the move. Attach
    // mode resolves no field order, so there the form-level list itself stays empty.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hiding_it_again_brings_the_gate_back(bool attach)
    {
        var account = new Account();
        var cut = RenderHost(account, attach);
        Assert.True((await SubmitAsync(cut)).CanProceed);
        await ApplyAsync(cut, new ValidationIssue("Code", CodeTaken));
        await ShowCodeAsync(cut, true);
        Assert.Empty(ModelMessages(cut, account));
        var runs = _rules.Runs;

        await ShowCodeAsync(cut, false);

        Assert.Equal([GateText], ModelMessages(cut, account));
        AssertGateOnEverySurface(cut, account);
        Assert.Equal(runs, _rules.Runs);

        await Services.DisposeAsync();
    }

    // A move that cannot change the gate republishes nothing. Note mounts and unmounts ten times:
    // after a reply on hidden Code (the gate shows); after a reply to a submit blocked by hidden
    // Code that names the form itself, whose error is always on screen (the gate stays down); and
    // after a second blocked submit whose revealed Code has since left (the gate shows). None of
    // these moves changes the gate, and nothing else the edit context holds turns on which fields
    // render, so the edit context hears nothing. Mutation that must break it: republish on every
    // move GateTurnsOnVisibility allows (drop the comparison with the store's gate), and each of
    // the ten moves notifies.
    [Theory]
    [InlineData("hidden-field")]
    [InlineData("model-level")]
    [InlineData("revealed-field")]
    public async Task Moves_that_cannot_change_the_gate_republish_nothing(string standing)
    {
        var account = new Account { Code = standing == "hidden-field" ? "A1" : string.Empty };
        var cut = RenderHost(account, note: true);
        var showsGate = standing != "model-level";
        if (standing == "hidden-field")
        {
            Assert.True((await SubmitAsync(cut)).CanProceed);
            await ApplyAsync(cut, new ValidationIssue("Code", CodeTaken));
        }
        else if (standing == "model-level")
        {
            Assert.False((await SubmitAsync(cut)).CanProceed);
            await ApplyAsync(cut, new ValidationIssue(string.Empty, OrderRejected));
        }
        else
        {
            await ShowCodeAsync(cut, true);
            Assert.False((await SubmitAsync(cut)).CanProceed);
            await ShowCodeAsync(cut, false);
            Assert.False((await SubmitAsync(cut)).CanProceed);
        }

        string[] expected = showsGate ? [GateText] : [OrderRejected];
        Assert.Equal(expected, ModelMessages(cut, account));
        var published = 0;
        Engine(cut).EditContext.OnValidationStateChanged += (_, _) => published++;

        for (var move = 0; move < 5; move++)
        {
            await ShowNoteAsync(cut, false);
            await ShowNoteAsync(cut, true);
        }

        Assert.Equal(0, published);
        Assert.Equal(expected, ModelMessages(cut, account));
        if (showsGate)
        {
            AssertGateOnEverySurface(cut, account);
        }
        else
        {
            Assert.DoesNotContain(Engine(cut).GetIssues(new FieldIdentifier(account, string.Empty)), i => i.Message == GateText);
        }

        await Services.DisposeAsync();
    }

    // Each move that does change the gate republishes once. After a reply on hidden Code, showing
    // Code takes the gate down, hiding it brings the gate back, and showing it again takes it down,
    // each with exactly one notification and every surface in step. Mutation that must break it:
    // leave standing server errors out of GateTurnsOnVisibility, and the hide notifies nothing.
    [Fact]
    public async Task Each_move_that_flips_the_gate_republishes_once()
    {
        var account = new Account();
        var cut = RenderHost(account);
        Assert.True((await SubmitAsync(cut)).CanProceed);
        await ApplyAsync(cut, new ValidationIssue("Code", CodeTaken));
        AssertGateOnEverySurface(cut, account);
        var published = 0;
        Engine(cut).EditContext.OnValidationStateChanged += (_, _) => published++;

        await ShowCodeAsync(cut, true);
        Assert.Equal(1, published);
        AssertNoGateAnywhere(cut, account);
        Assert.Equal([CodeTaken], KitMessages(cut, "Code"));

        await ShowCodeAsync(cut, false);
        Assert.Equal(2, published);
        AssertGateOnEverySurface(cut, account);

        await ShowCodeAsync(cut, true);
        Assert.Equal(3, published);
        AssertNoGateAnywhere(cut, account);

        await Services.DisposeAsync();
    }

    // An edit, and the whole-form re-check after it, leave the gate standing while the client still
    // fails the field. Code is empty and hidden, and the server rejects it. The re-check replaces
    // the reply with the client's own answer, which fails Code too, and the reply revealed Code, so
    // that answer shows wherever Code's errors show: the summary, when there is one. Mutation that
    // must break it: drop the arming from ApplyServerIssues, and no gate shows at any step.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_edit_and_recheck_keep_the_gate_while_the_client_still_fails_the_field(bool summary)
    {
        var account = new Account { Code = string.Empty };
        var cut = RenderHost(account, summary: summary);

        await ApplyAsync(cut, new ValidationIssue("Code", CodeTaken));
        Assert.Equal([GateText], ModelMessages(cut, account));

        await EditAsync(cut, "Name", "Bea");
        Assert.Equal([GateText], ModelMessages(cut, account));
        AssertGateOnEverySurface(cut, account);

        await RecheckAsync(cut);

        Assert.Equal([GateText], ModelMessages(cut, account));
        AssertGateOnEverySurface(cut, account);
        Assert.Equal([CodeRequired], Messages(Engine(cut).GetIssues(Code(account))));
        if (summary)
        {
            var entries = SummaryEntries(cut);
            Assert.Equal(2, entries.Count);
            Assert.Contains(CodeRequired, entries);
            Assert.Contains(GateText, entries);
        }

        await Services.DisposeAsync();
    }

    // The second route. A blocked submit for hidden Code shows the gate, then a reply naming only
    // Code arrives. The reply's error is no more on screen than the submit's, so the gate stays.
    // Mutation that must break it: restore GateActive's test that any standing server error takes
    // the gate down, and the gate goes at the reply, leaving nothing on screen.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reply_after_a_blocked_submit_keeps_the_gate(bool summary)
    {
        var account = new Account { Code = string.Empty };
        var cut = RenderHost(account, summary: summary);
        Assert.False((await SubmitAsync(cut)).CanProceed);
        Assert.Equal([GateText], ModelMessages(cut, account));

        await ApplyAsync(cut, new ValidationIssue("Code", CodeTaken));

        Assert.Equal([GateText], ModelMessages(cut, account));
        AssertGateOnEverySurface(cut, account);
        Assert.Contains(CodeTaken, Messages(Engine(cut).GetIssues(Code(account))));
        if (summary)
        {
            var entries = SummaryEntries(cut);
            Assert.Contains(CodeTaken, entries);
            Assert.Contains(GateText, entries);
        }

        await Services.DisposeAsync();
    }

    // A form with a summary lists the hidden field's error, and the gate shows beside it: the shape
    // a blocked submit for a hidden field already has, since a summary's listing does not count as
    // showing a field's error. Mutation that must break it: drop the arming from
    // ApplyServerIssues, and the summary lists Code's error alone.
    [Fact]
    public async Task With_a_summary_the_gate_shows_beside_the_hidden_fields_entry()
    {
        var account = new Account();
        var cut = RenderHost(account, summary: true);
        Assert.True((await SubmitAsync(cut)).CanProceed);

        await ApplyAsync(cut, new ValidationIssue("Code", CodeTaken));

        var entries = SummaryEntries(cut);
        Assert.Equal(2, entries.Count);
        Assert.Contains(CodeTaken, entries);
        Assert.Contains(GateText, entries);
        Assert.Equal([GateText], ModelMessages(cut, account));

        await Services.DisposeAsync();
    }

    // Control: a reply naming a field on screen shows its message there and no gate, whether or not
    // it also names a hidden field, and the reply's focus move asks for that field.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reply_naming_a_visible_field_shows_its_message_and_no_gate(bool alsoHidden)
    {
        var account = new Account();
        var cut = RenderHost(account);
        Assert.True((await SubmitAsync(cut)).CanProceed);
        var asked = _focus.Requests.Count;
        ValidationIssue[] reply = alsoHidden
            ? [new ValidationIssue("Name", NameTaken), new ValidationIssue("Code", CodeTaken)]
            : [new ValidationIssue("Name", NameTaken)];

        await ApplyAsync(cut, reply);

        Assert.Equal([NameTaken], KitMessages(cut, "Name"));
        AssertNoGateAnywhere(cut, account);
        Assert.Equal([FieldIdentifier.Create(() => account.Name)], _focus.Requests.Skip(asked).ToList());

        await Services.DisposeAsync();
    }

    // Control: a passing submit clears the reply and the gate together.
    [Fact]
    public async Task A_passing_submit_clears_the_reply_and_the_gate()
    {
        var account = new Account();
        var cut = RenderHost(account);
        Assert.True((await SubmitAsync(cut)).CanProceed);
        await ApplyAsync(cut, new ValidationIssue("Code", CodeTaken));
        Assert.Equal([GateText], ModelMessages(cut, account));

        Assert.True((await SubmitAsync(cut)).CanProceed);

        AssertNoGateAnywhere(cut, account);
        Assert.Empty(Engine(cut).GetIssues(Code(account)));
        Assert.Empty(Engine(cut).GetVisibleIssues());

        await Services.DisposeAsync();
    }

    private static FieldIdentifier Code(Account account) => FieldIdentifier.Create(() => account.Code);

    private static List<string> Messages(IEnumerable<ValidationIssue> issues) =>
        issues.Select(i => i.Message).ToList();

    private static void AssertGateOnEverySurface(IRenderedComponent<AccountHost> cut, Account account)
    {
        var engine = Engine(cut);
        var modelLevel = new FieldIdentifier(account, string.Empty);
        Assert.Contains(engine.GetIssues(modelLevel), i => i.Message == GateText);
        Assert.Contains(engine.GetVisibleIssues(), v => v.Field.Equals(modelLevel) && v.Issue.Message == GateText);
        Assert.Contains(engine.EditContext.GetValidationMessages(modelLevel), m => m == GateText);
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

    private IRenderedComponent<AccountHost> RenderHost(Account account, bool attach = false, bool summary = false, bool note = false) =>
        Render<AccountHost>(parameters => parameters
            .Add(p => p.Account, account)
            .Add(p => p.Attach, attach)
            .Add(p => p.Summary, summary)
            .Add(p => p.ShowNote, note));

    // Waits for any check the step started, then lets a reconcile or a posted republish run: the
    // dispatcher runs one piece of work at a time, so the queued no-op completes after them.
    private static async Task SettleAsync(IRenderedComponent<AccountHost> cut)
    {
        cut.WaitForState(() => !Engine(cut).IsValidating);
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => { });
    }

    private static async Task<SubmitOutcome> SubmitAsync(IRenderedComponent<AccountHost> cut)
    {
        var outcome = cut.Instance.Attach
            ? await cut.InvokeAsync(() => cut.FindComponent<FormidableValidator<Account>>().Instance.ValidateForSubmitAsync())
            : await cut.InvokeAsync(() => cut.FindComponent<FormidableForm<Account>>().Instance.SubmitAsync());
        await SettleAsync(cut);
        return outcome;
    }

    private static async Task ApplyAsync(IRenderedComponent<AccountHost> cut, params ValidationIssue[] issues)
    {
        if (cut.Instance.Attach)
        {
            await cut.InvokeAsync(() => cut.FindComponent<FormidableValidator<Account>>().Instance.ApplyServerIssues(issues));
        }
        else
        {
            await cut.InvokeAsync(() => cut.FindComponent<FormidableForm<Account>>().Instance.ApplyServerIssues(issues));
        }

        await SettleAsync(cut);
    }

    private static async Task EditAsync(IRenderedComponent<AccountHost> cut, string entry, string value)
    {
        await cut.InvokeAsync(() => cut.Find($"div[data-entry={entry}] input").Change(value));
        await SettleAsync(cut);
    }

    private static async Task ShowCodeAsync(IRenderedComponent<AccountHost> cut, bool show)
    {
        await cut.InvokeAsync(() => cut.Render(parameters => parameters.Add(p => p.ShowCode, show)));
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

    private static List<string> SummaryEntries(IRenderedComponent<AccountHost> cut) =>
        cut.FindAll("li.formidable-summary__item").Select(li => li.TextContent.Trim()).ToList();

    /// <summary>A name and a code, each valid until a test empties it, and a note no rule reads.</summary>
    public sealed class Account
    {
        public string? Name { get; set; } = "Ada";

        public string? Code { get; set; } = "A1";

        public string? Note { get; set; } = "note";
    }

    /// <summary>Both fields required, each rule counting its runs.</summary>
    private sealed class AccountValidator : AbstractValidator<Account>
    {
        public int Runs;

        public AccountValidator()
        {
            RuleFor(m => m.Name).Must(Counted).WithMessage("Name is required");
            RuleFor(m => m.Code).Must(Counted).WithMessage(CodeRequired);
        }

        private bool Counted(string? value)
        {
            Interlocked.Increment(ref Runs);
            return !string.IsNullOrEmpty(value);
        }
    }

    /// <summary>Records every field a focus move asks for, and lands each one.</summary>
    private sealed class RecordingFocus : IFormidableFocusService
    {
        public List<FieldIdentifier> Requests { get; } = [];

        public ValueTask<bool> FocusAsync(FieldIdentifier field)
        {
            Requests.Add(field);
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>Document order as a browser reports it: the form, which holds every field, first, then the fields in the order they were asked about.</summary>
    private sealed class DocumentOrder : IFormidableFieldOrderService
    {
        public ValueTask<IReadOnlyList<FieldIdentifier>?> OrderAsync(IReadOnlyList<FieldIdentifier> fields) =>
            ValueTask.FromResult<IReadOnlyList<FieldIdentifier>?>(
                fields.Where(f => f.FieldName.Length == 0).Concat(fields.Where(f => f.FieldName.Length != 0)).ToList());
    }

    /// <summary>A form over <see cref="Account"/>, on either root: an optional summary, a model-level list, a kit input for Name, a kit input for Code while <see cref="ShowCode"/> holds, and one for Note while <see cref="ShowNote"/> holds.</summary>
    private sealed class AccountHost : ComponentBase
    {
        [Parameter]
        public Account Account { get; set; } = default!;

        [Parameter]
        public bool Attach { get; set; }

        [Parameter]
        public bool Summary { get; set; }

        [Parameter]
        public bool ShowCode { get; set; }

        [Parameter]
        public bool ShowNote { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Attach)
            {
                builder.OpenComponent<EditForm>(0);
                builder.AddComponentParameter(1, nameof(EditForm.Model), Account);
                builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableValidator<Account>>(0);
                    inner.AddComponentParameter(1, nameof(FormidableValidator<Account>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Content));
                    inner.CloseComponent();
                }));
                builder.CloseComponent();
                return;
            }

            builder.OpenComponent<FormidableForm<Account>>(10);
            builder.AddComponentParameter(11, nameof(FormidableForm<Account>.Model), Account);
            builder.AddComponentParameter(12, nameof(FormidableForm<Account>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Content));
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

            KitField(builder, 10, "Name", () => Account.Name, v => Account.Name = v);
            if (ShowCode)
            {
                KitField(builder, 30, "Code", () => Account.Code, v => Account.Code = v);
            }

            if (ShowNote)
            {
                KitField(builder, 50, "Note", () => Account.Note, v => Account.Note = v);
            }
        }

        private void KitField(RenderTreeBuilder builder, int sequence, string entry, Expression<Func<string?>> field, Action<string?> set)
        {
            builder.OpenElement(sequence, "div");
            builder.AddAttribute(sequence + 1, "data-entry", entry);
            builder.OpenComponent<FormidableInputText>(sequence + 2);
            builder.AddComponentParameter(sequence + 3, "For", field);
            builder.AddComponentParameter(sequence + 4, "Value", field.Compile()());
            builder.AddComponentParameter(sequence + 5, "ValueChanged", EventCallback.Factory.Create<string?>(this, set));
            builder.CloseComponent();
            builder.OpenComponent<FormidableFieldMessage<string?>>(sequence + 6);
            builder.AddComponentParameter(sequence + 7, "For", field);
            builder.CloseComponent();
            builder.CloseElement();
        }
    }
}
