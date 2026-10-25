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
/// A server reply against a change in which fields the page renders. A field joining the form
/// leaves the reply standing, so a message only the server raises shows on a field opened after the
/// reply and stays through the whole-form re-check the opening starts. A field leaving the form lets
/// that re-check replace the reply, since the form it answers for may no longer be the one the
/// server judged.
/// </summary>
/// <remarks>
/// Every client rule passes the values these tests use, so every message after a reply is the
/// server's. The clock is a <see cref="FakeTimeProvider"/>: the re-check after a move runs only when
/// a test moves the clock past <see cref="FormidableOptions.RefreshDebounce"/>, and the rule counter
/// shows that it ran.
/// </remarks>
public class ServerReplyFieldSetTests : BunitContext
{
    private const string NameTaken = "Name is taken";
    private const string CodeTaken = "Code is taken";
    private const string SkuTaken = "SKU is taken";

    private static readonly TimeSpan PastRefresh = new FormidableOptions().RefreshDebounce + TimeSpan.FromMilliseconds(1);
    private static readonly string GateText = new FormidableOptions().DefensiveGateMessage;

    private readonly FakeTimeProvider _clock = new();
    private readonly OrderValidator _rules = new();

    public ServerReplyFieldSetTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddFormidableBlazor();
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddSingleton<IValidator<Order>>(_rules);
    }

    // The case the change exists for. A client submit passes, then the server rejects Code, which
    // the page does not render, so the form-level message explains the block. Opening Code shows the
    // server's message there and takes the form-level message down. The opening starts the whole-form
    // re-check, which runs the rules and leaves the reply standing, so the message is still there
    // after it, and still there long after. Under either root.
    // Mutation that must break it: let a re-check that began after any change in which fields render
    // replace the reply (compare the rendered-field-set generation again), and Code's message and the
    // summary entry go at the re-check, with nothing left on screen to explain the block.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opening_a_hidden_field_after_a_reply_keeps_the_replys_message(bool attach)
    {
        var order = new Order();
        var cut = RenderHost(order, attach);
        Assert.True((await SubmitAsync(cut)).CanProceed);
        await ApplyAsync(cut, new ValidationIssue(nameof(Order.Code), CodeTaken));
        Assert.Equal([GateText], ModelMessages(cut));

        await cut.InvokeAsync(() => cut.Render(parameters => parameters.Add(p => p.ShowCode, true)));
        await SettleAsync(cut);
        Assert.Equal([CodeTaken], KitMessages(cut, "Code"));
        Assert.Empty(ModelMessages(cut));
        var runs = _rules.Runs;

        await RecheckAsync(cut);
        Assert.True(_rules.Runs > runs, "the re-check the opening armed should have run the rules");
        Assert.Equal([CodeTaken], KitMessages(cut, "Code"));
        Assert.Equal([CodeTaken], SummaryEntries(cut));
        Assert.Empty(ModelMessages(cut));

        await cut.InvokeAsync(() => _clock.Advance(TimeSpan.FromSeconds(5)));
        await SettleAsync(cut);
        Assert.Equal([CodeTaken], KitMessages(cut, "Code"));

        await Services.DisposeAsync();
    }

    // A change that shows the field by removing another is a field leaving the form, as a step or a
    // tab that replaces one section with another is. Note renders and Code does not; the server
    // rejects Code, and one render then hides Note and shows Code. Code shows the server's message at
    // once. The re-check that change starts runs the rules and replaces the reply with the form's own
    // answer, which passes: no message on Code, an empty summary and no form-level message. Under
    // either root.
    // Mutation that must break it: count only departed fields the form counts as edited, and Code
    // keeps "Code is taken" after the re-check.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Showing_a_field_by_removing_another_lets_the_recheck_replace_the_reply(bool attach)
    {
        var order = new Order();
        var cut = RenderHost(order, attach, showNote: true);
        Assert.True((await SubmitAsync(cut)).CanProceed);
        await ApplyAsync(cut, new ValidationIssue(nameof(Order.Code), CodeTaken));
        Assert.Equal([GateText], ModelMessages(cut));

        await cut.InvokeAsync(() => cut.Render(parameters => parameters.Add(p => p.ShowNote, false).Add(p => p.ShowCode, true)));
        await SettleAsync(cut);
        Assert.Empty(cut.FindAll("div[data-entry=Note]"));
        Assert.Equal([CodeTaken], KitMessages(cut, "Code"));
        Assert.Empty(ModelMessages(cut));
        var runs = _rules.Runs;

        await RecheckAsync(cut);
        Assert.True(_rules.Runs > runs, "the re-check the swap armed should have run the rules");
        Assert.Empty(KitMessages(cut, "Code"));
        Assert.Empty(SummaryEntries(cut));
        Assert.Empty(ModelMessages(cut));

        await Services.DisposeAsync();
    }

    // A row leaving the page is a change to the model the server judged, made with no edit the form
    // hears of. The server rejects Name and the second line's SKU, and the page then removes that
    // line. Nothing ever typed in the line, so the form never counted it as edited. Until the
    // re-check the removal starts, the reply stands and Name keeps its message. The re-check runs the
    // rules and replaces the whole reply with the form's own answer, which passes: no message on
    // Name, none left for the removed line, an empty summary and no form-level message.
    // Mutation that must break it: count only departed fields the form counts as edited, and the
    // reply outlives the re-check, so Name keeps "Name is taken".
    [Fact]
    public async Task Removing_the_row_a_reply_named_lets_the_recheck_replace_it()
    {
        var order = new Order();
        var removed = order.Lines[1];
        var cut = RenderHost(order, attach: false);
        Assert.True((await SubmitAsync(cut)).CanProceed);
        await ApplyAsync(
            cut,
            new ValidationIssue(nameof(Order.Name), NameTaken),
            new ValidationIssue("Lines[1].Sku", SkuTaken));
        Assert.Equal([SkuTaken], KitMessages(cut, "Sku1"));

        await cut.InvokeAsync(() =>
        {
            order.Lines.RemoveAt(1);
            cut.Render();
        });
        await SettleAsync(cut);
        Assert.Empty(cut.FindAll("div[data-entry=Sku1]"));
        Assert.Equal([NameTaken], KitMessages(cut, "Name"));
        var runs = _rules.Runs;

        await RecheckAsync(cut);
        Assert.True(_rules.Runs > runs, "the re-check the removal armed should have run the rules");
        Assert.Empty(KitMessages(cut, "Name"));
        Assert.Empty(Engine(cut).GetIssues(FieldIdentifier.Create(() => removed.Sku)));
        Assert.Empty(SummaryEntries(cut));
        Assert.Empty(ModelMessages(cut));

        await Services.DisposeAsync();
    }

    // ================================================================ helpers ====

    private IRenderedComponent<OrderHost> RenderHost(Order order, bool attach, bool showNote = false) =>
        Render<OrderHost>(parameters => parameters
            .Add(p => p.Order, order)
            .Add(p => p.Attach, attach)
            .Add(p => p.ShowNote, showNote));

    // Waits for any check the step started, then lets a reconcile or a posted republish run: the
    // dispatcher runs one piece of work at a time, so each queued no-op completes after them.
    private static async Task SettleAsync(IRenderedComponent<OrderHost> cut)
    {
        cut.WaitForState(() => !Engine(cut).IsValidating);
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => { });
    }

    private async Task RecheckAsync(IRenderedComponent<OrderHost> cut)
    {
        await cut.InvokeAsync(() => _clock.Advance(PastRefresh));
        await SettleAsync(cut);
    }

    private static async Task<SubmitOutcome> SubmitAsync(IRenderedComponent<OrderHost> cut)
    {
        var outcome = cut.Instance.Attach
            ? await cut.InvokeAsync(() => cut.FindComponent<FormidableValidator<Order>>().Instance.ValidateForSubmitAsync())
            : await cut.InvokeAsync(() => cut.FindComponent<FormidableForm<Order>>().Instance.SubmitAsync());
        await SettleAsync(cut);
        return outcome;
    }

    private static async Task ApplyAsync(IRenderedComponent<OrderHost> cut, params ValidationIssue[] issues)
    {
        if (cut.Instance.Attach)
        {
            await cut.InvokeAsync(() => cut.FindComponent<FormidableValidator<Order>>().Instance.ApplyServerIssues(issues));
        }
        else
        {
            await cut.InvokeAsync(() => cut.FindComponent<FormidableForm<Order>>().Instance.ApplyServerIssues(issues));
        }

        await SettleAsync(cut);
    }

    private static IFormidableEngine Engine(IRenderedComponent<OrderHost> cut) =>
        cut.Instance.Attach
            ? cut.FindComponent<FormidableValidator<Order>>().Instance.Engine!
            : cut.FindComponent<FormidableForm<Order>>().Instance.Engine!;

    private static List<string> ModelMessages(IRenderedComponent<OrderHost> cut)
    {
        var id = FormidableFieldId.MessagesFor(new FieldIdentifier(cut.Instance.Order, string.Empty));
        var list = Assert.Single(cut.FindAll("ul"), ul => ul.Id == id);
        return list.Children.Select(li => li.TextContent.Trim()).ToList();
    }

    private static List<string> KitMessages(IRenderedComponent<OrderHost> cut, string entry) =>
        cut.FindAll($"div[data-entry={entry}] li").Select(li => li.TextContent.Trim()).ToList();

    private static List<string> SummaryEntries(IRenderedComponent<OrderHost> cut) =>
        cut.FindAll("li.formidable-summary__item").Select(li => li.TextContent.Trim()).ToList();

    /// <summary>A name, a code, a note no rule reads and two lines, every value one the client rules accept.</summary>
    public sealed class Order
    {
        public string? Name { get; set; } = "Ada";

        public string? Code { get; set; } = "A1";

        public string? Note { get; set; } = "note";

        public List<OrderLine> Lines { get; } = [new() { Sku = "S1" }, new() { Sku = "S2" }];
    }

    /// <summary>One line of an <see cref="Order"/>.</summary>
    public sealed class OrderLine
    {
        public string? Sku { get; set; }
    }

    /// <summary>Every value required, each rule counting its runs.</summary>
    private sealed class OrderValidator : AbstractValidator<Order>
    {
        private int _runs;

        public OrderValidator()
        {
            RuleFor(o => o.Name).Must(Counted).WithMessage("Name is required");
            RuleFor(o => o.Code).Must(Counted).WithMessage("Code is required");
            RuleForEach(o => o.Lines).ChildRules(line => line.RuleFor(l => l.Sku).Must(Counted).WithMessage("SKU is required"));
        }

        /// <summary>How many rule runs have started.</summary>
        public int Runs => Volatile.Read(ref _runs);

        private bool Counted(string? value)
        {
            Interlocked.Increment(ref _runs);
            return !string.IsNullOrEmpty(value);
        }
    }

    /// <summary>A form over <see cref="Order"/>, on either root: a summary, a model-level list, a kit input for Name, one for Code while <see cref="ShowCode"/> holds, one for Note while <see cref="ShowNote"/> holds, and one keyed row per line.</summary>
    private sealed class OrderHost : ComponentBase
    {
        [Parameter]
        public Order Order { get; set; } = default!;

        [Parameter]
        public bool Attach { get; set; }

        [Parameter]
        public bool ShowCode { get; set; }

        [Parameter]
        public bool ShowNote { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Attach)
            {
                builder.OpenComponent<EditForm>(0);
                builder.AddComponentParameter(1, nameof(EditForm.Model), Order);
                builder.AddComponentParameter(2, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableValidator<Order>>(0);
                    inner.AddComponentParameter(1, nameof(FormidableValidator<Order>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Content));
                    inner.CloseComponent();
                }));
                builder.CloseComponent();
                return;
            }

            builder.OpenComponent<FormidableForm<Order>>(10);
            builder.AddComponentParameter(11, nameof(FormidableForm<Order>.Model), Order);
            builder.AddComponentParameter(12, nameof(FormidableForm<Order>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Content));
            builder.CloseComponent();
        }

        private void Content(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableSummary>(0);
            builder.CloseComponent();

            builder.OpenComponent<FormidableModelMessage>(1);
            builder.CloseComponent();

            KitField(builder, 10, "Name", key: null, () => Order.Name, v => Order.Name = v);
            if (ShowCode)
            {
                KitField(builder, 30, "Code", key: null, () => Order.Code, v => Order.Code = v);
            }

            if (ShowNote)
            {
                KitField(builder, 40, "Note", key: null, () => Order.Note, v => Order.Note = v);
            }

            for (var i = 0; i < Order.Lines.Count; i++)
            {
                var line = Order.Lines[i];
                KitField(builder, 50, $"Sku{i}", key: line, () => line.Sku, v => line.Sku = v);
            }
        }

        private void KitField(RenderTreeBuilder builder, int sequence, string entry, object? key, Expression<Func<string?>> field, Action<string?> set)
        {
            builder.OpenElement(sequence, "div");
            if (key is not null)
            {
                builder.SetKey(key);
            }

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
