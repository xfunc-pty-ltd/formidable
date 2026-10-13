using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
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
/// A page marks a request as it sends it, and applies the reply with that mark. A reply the
/// visitor has edited past is dropped, and so is one after a load, a change to which fields
/// render, or a reset. A dropped reply changes nothing at all and the apply answers false. A
/// reply with none of those since its mark applies exactly as an unmarked one does, and an
/// unmarked apply applies every reply, whenever it arrives.
/// </summary>
/// <remarks>
/// Most forms here are judged by their server, as /server is: the live profile is the empty
/// draft profile, so an edit runs no rule, and the form's own answer for every rule is the one its
/// opening re-check found. Each rule counts its runs. The clock is a <see cref="FakeTimeProvider"/>,
/// so a re-check runs only when a test moves the clock, and every focus request is recorded.
/// </remarks>
public class ServerRequestMarkTests : BunitContext
{
    private const string NameRequired = "Name is required";
    private const string CodeRequired = "Code is required";

    private static readonly TimeSpan PastRefresh = new FormidableOptions().RefreshDebounce + TimeSpan.FromMilliseconds(1);

    private readonly FakeTimeProvider _clock = new();
    private readonly RecordingFocusService _focus = new();

    public ServerRequestMarkTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Ahead of AddFormidableBlazor, whose focus registration is a TryAdd, so this one stands.
        Services.AddSingleton<IFormidableFocusService>(_focus);
        Services.AddFormidableBlazor();
        Services.AddSingleton<TimeProvider>(_clock);
    }

    // ================================================================ dropped ====

    // The mark is taken, the visitor commits an edit, then the reply to the marked request lands.
    // Whichever field the edit touched, and whether it came from an input or from the page's own
    // notification, the reply is dropped. Every private field of the engine reads the same after
    // the call as before it, so nothing was applied, revealed, held back, armed or cleared. No
    // event is raised, no focus moves, HasSubmitted stays false, and moving the clock past
    // RefreshDebounce runs no rule, so no re-check was armed. On the judged form an applied reply
    // would have held Code back and armed a re-check at once, and named only hidden Note it would
    // have raised the form's own message. Where a blocked submit has already left Name's error
    // showing, the form's apply would have moved focus to it; a dropped reply moves none.
    // Mutations that must break it: ignore the mark in the engine's marked apply, and every row
    // returns true with the reply on screen; let the form's marked apply move focus on a drop, and
    // the rows with an error showing record a move.
    [Theory]
    [InlineData("form", "Code", "judged", "Code", false)]
    [InlineData("form", "Name", "judged", "Code", false)]
    [InlineData("form", "notify", "judged", "Code", false)]
    [InlineData("form", "Code", "default", "Code", false)]
    [InlineData("form", "Code", "judged", "Note", false)]
    [InlineData("form", "Code", "judged", "Code", true)]
    [InlineData("form-problem", "Code", "judged", "Code", false)]
    [InlineData("form-problem", "Code", "default", "Code", true)]
    [InlineData("engine", "Code", "judged", "Code", false)]
    [InlineData("validator", "Code", "judged", "Code", false)]
    [InlineData("validator-problem", "Name", "judged", "Code", false)]
    public async Task A_reply_marked_before_an_edit_is_dropped_and_changes_nothing(string route, string edit, string config, string replyNames, bool errorShowing)
    {
        var model = new Reservation();
        var rules = new ReservationRules();
        var cut = RenderHost(model, rules, Options(config), attach: route.StartsWith("validator", StringComparison.Ordinal));
        await SettleAsync(cut);
        if (errorShowing)
        {
            await EditAsync(cut, "Name", string.Empty);
            Assert.False((await cut.InvokeAsync(() => cut.FindComponent<FormidableForm<Reservation>>().Instance.SubmitAsync())).CanProceed);
            await SettleAsync(cut);
            Assert.Equal([NameRequired], Messages(Engine(cut), FieldIdentifier.Create(() => model.Name)));
        }

        var sentAt = await MarkAsync(cut, route);
        switch (edit)
        {
            case "notify":
                await cut.InvokeAsync(() => Engine(cut).EditContext.NotifyFieldChanged(FieldIdentifier.Create(() => model.Code)));
                break;
            default:
                await EditAsync(cut, edit, "B2");
                break;
        }

        await SettleAsync(cut);
        var engine = Engine(cut);
        var runs = rules.Runs;
        var focus = _focus.Requests.Count;
        var stateChanges = 0;
        var validationStateChanges = 0;
        engine.StateChanged += (_, _) => stateChanges++;
        engine.EditContext.OnValidationStateChanged += (_, _) => validationStateChanges++;
        var visible = Describe(engine.GetVisibleIssues());
        var messages = engine.EditContext.GetValidationMessages().ToList();

        string? before = null;
        string? after = null;
        var applied = await cut.InvokeAsync(() =>
        {
            before = Snapshot(engine);
            var result = Apply(cut, route, sentAt, new ValidationIssue(replyNames, $"{replyNames} A1 is taken"));
            after = Snapshot(engine);
            return result;
        });

        Assert.False(applied);
        Assert.Equal(before, after);
        Assert.Equal(0, stateChanges);
        Assert.Equal(0, validationStateChanges);
        Assert.Equal(focus, _focus.Requests.Count);
        Assert.Equal(errorShowing, engine.HasSubmitted);
        Assert.Equal(visible, Describe(engine.GetVisibleIssues()));
        Assert.Equal(messages, engine.EditContext.GetValidationMessages());

        await SettleAsync(cut);
        Assert.Equal(runs, rules.Runs);
        Assert.Equal(visible, Describe(engine.GetVisibleIssues()));
        Assert.Equal(focus, _focus.Requests.Count);
        await Services.DisposeAsync();
    }

    // Two sends overlap: the first goes out with Code at A1, the visitor changes Code to B2, and the
    // second goes out. Whichever reply arrives first, the first send's is dropped and the second's
    // applies, so B2's reply is the one standing and A1's never shows.
    // Mutation that must break it: ignore the mark, and the older-last rows leave "Code A1 is taken"
    // standing over B2.
    [Theory]
    [InlineData("form", true)]
    [InlineData("form", false)]
    [InlineData("engine", true)]
    [InlineData("engine", false)]
    [InlineData("validator", true)]
    [InlineData("validator", false)]
    public async Task Overlapping_replies_keep_only_the_current_one_in_either_order(string route, bool olderLast)
    {
        var model = new Reservation();
        var rules = new ReservationRules();
        var cut = RenderHost(model, rules, Options("judged"), attach: route == "validator");
        await SettleAsync(cut);
        var code = FieldIdentifier.Create(() => model.Code);

        var first = await MarkAsync(cut, route);
        await EditAsync(cut, "Code", "B2");
        await SettleAsync(cut);
        var second = await MarkAsync(cut, route);

        var applied = new List<bool>();
        async Task ReplyAsync(ServerRequestMark sentAt, string sentCode)
        {
            applied.Add(await cut.InvokeAsync(() => Apply(cut, route, sentAt, new ValidationIssue("Code", $"Code {sentCode} is taken"))));
            await SettleAsync(cut);
        }

        if (olderLast)
        {
            await ReplyAsync(second, "B2");
            await ReplyAsync(first, "A1");
            Assert.Equal([true, false], applied);
        }
        else
        {
            await ReplyAsync(first, "A1");
            await ReplyAsync(second, "B2");
            Assert.Equal([false, true], applied);
        }

        Assert.Equal(["Code B2 is taken"], Messages(Engine(cut), code));
        Assert.DoesNotContain(Engine(cut).GetVisibleIssues(), v => v.Issue.Message.Contains("A1", StringComparison.Ordinal));
        await Services.DisposeAsync();
    }

    // ================================================================ applied ====

    // Nothing that drops a reply happens between the mark and the reply: nothing at all, the
    // visitor entering and leaving Code without changing it, a client submit, or a render that
    // changes no field. The reply applies and answers true: it stands on Code, HasSubmitted is
    // set, and the form's apply moves focus while attach mode and the engine move none. Entering
    // and leaving runs no rule.
    // Mutation that must break it: drop the reply whenever the field set's generation or the edit
    // stamp is anything but zero, and every row fails.
    [Theory]
    [InlineData("form", "nothing")]
    [InlineData("form", "enter-and-leave")]
    [InlineData("form", "submit")]
    [InlineData("form", "render")]
    [InlineData("form-problem", "nothing")]
    [InlineData("engine", "nothing")]
    [InlineData("validator", "nothing")]
    [InlineData("validator", "enter-and-leave")]
    [InlineData("validator-problem", "render")]
    public async Task A_reply_with_no_edit_since_its_mark_applies(string route, string between)
    {
        var model = new Reservation();
        var rules = new ReservationRules();
        var attach = route.StartsWith("validator", StringComparison.Ordinal);
        var cut = RenderHost(model, rules, Options("judged"), attach, blurCommitsCode: between == "enter-and-leave");
        await SettleAsync(cut);
        var code = FieldIdentifier.Create(() => model.Code);

        var sentAt = await MarkAsync(cut, route);
        var runs = rules.Runs;
        switch (between)
        {
            case "enter-and-leave":
                // Code commits on blur on this form, so leaving it is the moment a commit would
                // happen. Nothing was typed, so none does.
                await cut.InvokeAsync(() => cut.Find("[data-entry=\"Code\"] input").Blur());
                await SettleAsync(cut);
                Assert.Equal(runs, rules.Runs);
                break;
            case "submit":
                Assert.True((await cut.InvokeAsync(() => cut.FindComponent<FormidableForm<Reservation>>().Instance.SubmitAsync())).CanProceed);
                await SettleAsync(cut);
                break;
            case "render":
                await cut.InvokeAsync(() => cut.Render(p => p.Add(h => h.Caption, "Sending")));
                await SettleAsync(cut);
                break;
        }

        var focus = _focus.Requests.Count;
        var applied = await cut.InvokeAsync(() => Apply(cut, route, sentAt, new ValidationIssue("Code", "Code A1 is taken")));
        await cut.InvokeAsync(() => { });

        Assert.True(applied);
        Assert.Equal(["Code A1 is taken"], Messages(Engine(cut), code));
        Assert.True(Engine(cut).HasSubmitted);
        var moves = _focus.Requests.Skip(focus).ToList();
        if (route.StartsWith("form", StringComparison.Ordinal))
        {
            Assert.Equal([code], moves);
        }
        else
        {
            Assert.Empty(moves);
        }

        await Services.DisposeAsync();
    }

    // A reply that is current applies exactly as the unmarked apply does. Two forms run the same
    // steps side by side on one clock: the opening re-check, an edit to Code (which on this judged
    // form leaves the form's own answer older than the edit), then the reply, marked on one form
    // and unmarked on the other. Every private field of the two engines, every message, every focus
    // request and every event match, and so does what the re-check the reply arms does: it runs
    // Code's and Name's rules once each, on both forms.
    // Mutation that must break it: skip the focus move in the form's marked apply, and the focus
    // requests differ.
    [Theory]
    [InlineData("form")]
    [InlineData("form-problem")]
    [InlineData("validator")]
    [InlineData("engine")]
    public async Task A_current_marked_reply_matches_an_unmarked_apply(string route)
    {
        var attach = route.StartsWith("validator", StringComparison.Ordinal);
        var markedModel = new Reservation();
        var plainModel = new Reservation();
        var markedRules = new ReservationRules();
        var plainRules = new ReservationRules();
        var marked = RenderHost(markedModel, markedRules, Options("judged"), attach);
        var plain = RenderHost(plainModel, plainRules, Options("judged"), attach);
        await SettleAsync(marked);
        await SettleAsync(plain);

        await EditAsync(marked, "Code", "B2");
        await EditAsync(plain, "Code", "B2");
        await SettleAsync(marked);
        await SettleAsync(plain);
        var sentAt = await MarkAsync(marked, route);

        var events = new Dictionary<string, int> { ["marked"] = 0, ["plain"] = 0 };
        Engine(marked).StateChanged += (_, _) => events["marked"]++;
        Engine(plain).StateChanged += (_, _) => events["plain"]++;
        var markedRuns = (markedRules.CodeRuns, markedRules.NameRuns);
        var plainRuns = (plainRules.CodeRuns, plainRules.NameRuns);

        var reply = new ValidationIssue("Code", "Code B2 is taken");
        var applied = await marked.InvokeAsync(() => Apply(marked, route, sentAt, reply));
        await plain.InvokeAsync(() => ApplyUnmarked(plain, route, reply));
        Assert.True(applied);
        Assert.Equal(Snapshot(Engine(plain)), Snapshot(Engine(marked)));
        Assert.Equal(events["plain"], events["marked"]);
        Assert.Equal(Describe(Engine(plain).GetVisibleIssues()), Describe(Engine(marked).GetVisibleIssues()));
        Assert.Equal(FocusFor(plainModel), FocusFor(markedModel));

        await SettleAsync(marked);
        await SettleAsync(plain);
        Assert.Equal(Snapshot(Engine(plain)), Snapshot(Engine(marked)));
        Assert.Equal(Describe(Engine(plain).GetVisibleIssues()), Describe(Engine(marked).GetVisibleIssues()));
        Assert.Equal((markedRuns.CodeRuns + 1, markedRuns.NameRuns + 1), (markedRules.CodeRuns, markedRules.NameRuns));
        Assert.Equal((plainRuns.CodeRuns + 1, plainRuns.NameRuns + 1), (plainRules.CodeRuns, plainRules.NameRuns));
        Assert.Equal(["Code B2 is taken"], Messages(Engine(marked), FieldIdentifier.Create(() => markedModel.Code)));
        await Services.DisposeAsync();
    }

    // ================================================================ loads, field-set changes and resets ====

    // The mark is taken, then the page loads values, a component that renders a field arrives or
    // leaves, or the form is reset (attach mode: the page hands its EditForm a new EditContext).
    // The reply is dropped: the apply answers false, and the form as it now stands shows nothing,
    // has not submitted, and moved no focus. An engine the page held before a reset drops the
    // reply too.
    // Mutation that must break it: leave the generation out of the mark, and the field-set rows
    // apply; leave the engine out, and the reset rows apply.
    [Theory]
    [InlineData("form", "load")]
    [InlineData("form", "show-note")]
    [InlineData("form", "hide-note")]
    [InlineData("form", "anchor-code")]
    [InlineData("form", "reset")]
    [InlineData("form-problem", "reset")]
    [InlineData("engine", "load")]
    [InlineData("engine", "show-note")]
    [InlineData("engine", "reset")]
    [InlineData("engine-held", "reset")]
    [InlineData("validator", "load")]
    [InlineData("validator", "show-note")]
    [InlineData("validator", "new-edit-context")]
    [InlineData("validator-problem", "new-edit-context")]
    public async Task A_load_a_field_set_change_or_a_reset_after_the_mark_drops_the_reply(string route, string change)
    {
        var model = new Reservation();
        var rules = new ReservationRules();
        var attach = route.StartsWith("validator", StringComparison.Ordinal);
        var cut = RenderHost(model, rules, Options("judged"), attach, showNote: change == "hide-note");
        await SettleAsync(cut);
        var code = FieldIdentifier.Create(() => model.Code);

        var held = Engine(cut);
        var sentAt = await MarkAsync(cut, route == "engine-held" ? "engine" : route);
        switch (change)
        {
            case "load":
                await cut.InvokeAsync(() => Engine(cut).DiscloseLoadedValuesAsync());
                break;
            case "show-note":
            case "hide-note":
                await cut.InvokeAsync(() => cut.Render(p => p.Add(h => h.ShowNote, change == "show-note")));
                break;
            case "anchor-code":
                // A second component for Code, which is already on screen: the fields on screen are
                // the same before and after, and a component arriving still counts.
                await cut.InvokeAsync(() => cut.Render(p => p.Add(h => h.AnchorCode, true)));
                break;
            case "reset":
                await cut.InvokeAsync(() => cut.FindComponent<FormidableForm<Reservation>>().Instance.ResetAsync());
                break;
            case "new-edit-context":
                await cut.InvokeAsync(() => cut.Render(p => p.Add(h => h.AttachContext, new EditContext(model))));
                break;
        }

        await SettleAsync(cut);
        var focus = _focus.Requests.Count;
        var issue = new ValidationIssue("Code", "Code A1 is taken");
        var applied = await cut.InvokeAsync(() => route == "engine-held"
            ? held.ApplyServerIssues([issue], sentAt)
            : Apply(cut, route, sentAt, issue));
        await SettleAsync(cut);

        Assert.False(applied);
        Assert.Empty(Messages(Engine(cut), code));
        Assert.Empty(Engine(cut).GetVisibleIssues());
        Assert.False(Engine(cut).HasSubmitted);
        Assert.Equal(focus, _focus.Requests.Count);
        await Services.DisposeAsync();
    }

    // ================================================================ unmarked and default marks ====

    // The control: without a mark, a reply to a request sent before an edit lands and stands over
    // the newer value, and overlapping sends whose older reply arrives last leave that older reply
    // standing. The default mark marks nothing, so it applies the same way.
    [Theory]
    [InlineData("unmarked", "edit-in-flight")]
    [InlineData("unmarked", "overlap-older-last")]
    [InlineData("default-mark", "edit-in-flight")]
    [InlineData("default-mark", "overlap-older-last")]
    public async Task An_unmarked_apply_keeps_2_0_0_behaviour(string apply, string scenario)
    {
        var model = new Reservation();
        var rules = new ReservationRules();
        var cut = RenderHost(model, rules, Options("judged"), attach: false);
        await SettleAsync(cut);
        var form = cut.FindComponent<FormidableForm<Reservation>>().Instance;
        var code = FieldIdentifier.Create(() => model.Code);

        bool Reply(string sentCode)
        {
            var issue = new ValidationIssue("Code", $"Code {sentCode} is taken");
            if (apply == "default-mark")
            {
                return form.ApplyServerIssues([issue], default);
            }

            form.ApplyServerIssues([issue]);
            return true;
        }

        await EditAsync(cut, "Code", "B2");
        await SettleAsync(cut);
        if (scenario == "overlap-older-last")
        {
            Assert.True(await cut.InvokeAsync(() => Reply("B2")));
            await SettleAsync(cut);
        }

        var focus = _focus.Requests.Count;
        Assert.True(await cut.InvokeAsync(() => Reply("A1")));
        await SettleAsync(cut);

        Assert.Equal("B2", model.Code);
        Assert.Equal(["Code A1 is taken"], Messages(Engine(cut), code));
        Assert.True(Engine(cut).HasSubmitted);
        Assert.Equal([code], _focus.Requests.Skip(focus));
        await Services.DisposeAsync();
    }

    // An engine written before the mark existed implements neither new member. Its mark is the
    // default one and its marked apply is the interface's own: it applies the reply through the
    // unmarked member and answers true, whatever came after the mark.
    [Fact]
    public async Task An_engine_without_the_mark_applies_every_marked_reply()
    {
        var model = new Reservation();
        var rules = new ReservationRules();
        var cut = RenderHost(model, rules, Options("judged"), attach: false);
        await SettleAsync(cut);
        IFormidableEngine older = new EngineWithoutTheMark(Engine(cut));

        var sentAt = await cut.InvokeAsync(() => older.MarkServerRequest());
        await EditAsync(cut, "Code", "B2");
        await SettleAsync(cut);
        var applied = await cut.InvokeAsync(() => older.ApplyServerIssues([new ValidationIssue("Code", "Code A1 is taken")], sentAt));

        Assert.Equal(default, sentAt);
        Assert.True(applied);
        Assert.Equal(["Code A1 is taken"], Messages(Engine(cut), FieldIdentifier.Create(() => model.Code)));
        await Services.DisposeAsync();
    }

    // Once a root is disposed its mark is the default one and its marked apply answers false,
    // changing nothing, as every late call on a disposed root does nothing. A null reply still
    // throws first.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_disposed_root_marks_nothing_and_drops_the_reply(bool attach)
    {
        var model = new Reservation();
        var cut = RenderHost(model, new ReservationRules(), Options("judged"), attach);
        await SettleAsync(cut);
        var sentAt = await MarkAsync(cut, attach ? "validator" : "form");
        var form = attach ? null : cut.FindComponent<FormidableForm<Reservation>>().Instance;
        var validator = attach ? cut.FindComponent<FormidableValidator<Reservation>>().Instance : null;
        await cut.InvokeAsync(() => cut.Render(p => p.Add(h => h.Hidden, true)));

        var issues = new[] { new ValidationIssue("Code", "Code A1 is taken") };
        var problem = new FormidableValidationProblem { Errors = { ["Code"] = ["Code A1 is taken"] } };
        await cut.InvokeAsync(() =>
        {
            Assert.Equal(default, form?.MarkServerRequest() ?? validator!.MarkServerRequest());
            Assert.False(form?.ApplyServerIssues(issues, sentAt) ?? validator!.ApplyServerIssues(issues, sentAt));
            Assert.False(form?.ApplyServerIssues(problem, sentAt) ?? validator!.ApplyServerIssues(problem, sentAt));
            Assert.Throws<ArgumentNullException>(() => form?.ApplyServerIssues((IEnumerable<ValidationIssue>)null!, sentAt) ?? validator!.ApplyServerIssues((IEnumerable<ValidationIssue>)null!, sentAt));
            Assert.Throws<ArgumentNullException>(() => form?.ApplyServerIssues((FormidableValidationProblem)null!, sentAt) ?? validator!.ApplyServerIssues((FormidableValidationProblem)null!, sentAt));
        });
        await Services.DisposeAsync();
    }

    // ================================================================ a page that changes the field set as it sends ====

    // A page that drops blank rows before it sends (as /server's normalizing does) changes which
    // fields render, and that render comes after the click handler yields. A mark taken before the
    // yield is older than the change, so the reply is dropped. A page that yields once before
    // taking the mark lets the render happen first, and the root takes the change in as it marks,
    // so the reply applies to the rows it names, on either root.
    // Mutation that must break it: take nothing in at the mark, and the attach-mode row with the
    // yield drops the reply, since the validator takes a render's changes in from a later post.
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task A_row_the_page_drops_as_it_sends_needs_its_render_before_the_mark(bool attach, bool yieldFirst, bool expected)
    {
        var cut = Render<SendingPage>(p => p.Add(x => x.YieldBeforeMark, yieldFirst).Add(x => x.Attach, attach));
        var page = cut.Instance;
        await cut.InvokeAsync(() => _clock.Advance(PastRefresh));
        cut.WaitForState(() => !page.Engine!.IsValidating);
        await cut.InvokeAsync(() => cut.Find("[data-entry=\"Sku0\"] input").Change("   "));

        await cut.InvokeAsync(() => cut.Find("button").Click());
        cut.WaitForState(() => page.Reply is not null);
        Assert.Single(page.Lines);
        Assert.Single(cut.FindAll("[data-entry^=\"Sku\"]"));

        await cut.InvokeAsync(() => page.Reply!.SetResult([new ValidationIssue("Lines[0].Sku", "SKU is required")]));
        cut.WaitForState(() => page.Applied is not null);

        Assert.Equal(expected, page.Applied);
        var sku = new FieldIdentifier(page.Lines[0], nameof(OrderLine.Sku));
        Assert.Equal(expected ? ["SKU is required"] : [], Messages(page.Engine!, sku));
        await Services.DisposeAsync();
    }

    // A field change that has rendered but that the root has not yet taken in. The validator takes
    // a render's changes in from a post that runs after the render, and on Blazor Server the form
    // takes them in only once the browser acknowledges the render, which bUnit cannot hold back. A
    // nested component that hides its own field leaves the form out of the render, so the form
    // posts too, which is the shared shape: the root's count of changes taken in has not moved
    // when the page marks. The mark takes the change in first, so a reply with nothing changed
    // since applies. A mark taken in the page's own first OnAfterRender, which runs before the
    // root's, is the same shape on the first render.
    // Mutation that must break it: take nothing in at the mark, and every row drops the reply.
    [Theory]
    [InlineData(false, "nested-hide")]
    [InlineData(true, "nested-hide")]
    [InlineData(false, "first-after-render")]
    [InlineData(true, "first-after-render")]
    public async Task A_root_takes_in_a_rendered_field_change_before_it_marks(bool attach, string shape)
    {
        ServerRequestMark sentAt;
        IRenderedComponent<IComponent> cut;
        if (shape == "first-after-render")
        {
            var page = Render<SendingPage>(p => p.Add(x => x.Attach, attach).Add(x => x.MarkOnFirstRender, true));
            cut = page;
            cut.WaitForState(() => page.Instance.FirstMark is not null);
            sentAt = page.Instance.FirstMark!.Value;
            await cut.InvokeAsync(() => _clock.Advance(PastRefresh));
            cut.WaitForState(() => !page.Instance.Engine!.IsValidating);
            await cut.InvokeAsync(() => { });
            var line = page.Instance.Lines[0];
            var applied = await cut.InvokeAsync(() => page.Instance.Apply([new ValidationIssue("Lines[0].Sku", "SKU is required")], sentAt));
            Assert.True(applied);
            Assert.Equal(["SKU is required"], Messages(page.Instance.Engine!, new FieldIdentifier(line, nameof(OrderLine.Sku))));
            await Services.DisposeAsync();
            return;
        }

        var model = new Reservation();
        var host = RenderHost(model, new ReservationRules(), Options("judged"), attach, nestNote: true);
        cut = host;
        await SettleAsync(host);
        var reconciles = ReconcileCount(host);

        sentAt = await host.InvokeAsync(() =>
        {
            host.FindComponent<NestedNote>().Instance.Hide();
            Assert.Equal(reconciles, ReconcileCount(host));
            return MarkThrough(host, attach);
        });
        await SettleAsync(host);

        var result = await host.InvokeAsync(() => Apply(host, attach ? "validator" : "form", sentAt, new ValidationIssue("Code", "Code A1 is taken")));
        Assert.True(result);
        Assert.Equal(["Code A1 is taken"], Messages(Engine(host), FieldIdentifier.Create(() => model.Code)));
        await Services.DisposeAsync();
    }

    // The reply's side of the same shape: the field change renders after the mark, and the reply
    // lands before the root has taken it in. A nested component hides its own field and the reply
    // is applied in the same work item, ahead of the root's posted take-in, which is the state the
    // form is in on Blazor Server until the browser acknowledges a render. The marked apply takes
    // the change in first, so the reply is dropped: nothing shows, nothing is counted as a submit
    // and focus stays.
    // Mutation that must break it: take nothing in at the marked apply, and both rows apply.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_field_change_rendered_after_the_mark_drops_a_reply_the_root_has_not_taken_it_in_for(bool attach)
    {
        var model = new Reservation();
        var host = RenderHost(model, new ReservationRules(), Options("judged"), attach, nestNote: true);
        await SettleAsync(host);
        var sentAt = await host.InvokeAsync(() => MarkThrough(host, attach));
        await SettleAsync(host);
        var reconciles = ReconcileCount(host);
        var focus = _focus.Requests.Count;

        var applied = await host.InvokeAsync(() =>
        {
            host.FindComponent<NestedNote>().Instance.Hide();
            Assert.Equal(reconciles, ReconcileCount(host));
            return Apply(host, attach ? "validator" : "form", sentAt, new ValidationIssue("Code", "Code A1 is taken"));
        });
        await SettleAsync(host);

        Assert.False(applied);
        Assert.DoesNotContain("Code A1 is taken", Messages(Engine(host), FieldIdentifier.Create(() => model.Code)));
        Assert.False(Engine(host).HasSubmitted);
        Assert.Equal(focus, _focus.Requests.Count);
        await Services.DisposeAsync();
    }

    // The default mark marks nothing, so its apply takes nothing in and matches the unmarked
    // apply: the change is taken in after the reply, and the whole-form re-check it starts
    // replaces the reply, as it does for an unmarked one.
    // Mutation that must break it: take the change in for the default mark too, and the reply
    // outlives the re-check.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_default_mark_takes_no_field_change_in_before_its_reply(bool attach, bool defaultMark)
    {
        var model = new Reservation();
        var host = RenderHost(model, new ReservationRules(), Options("judged"), attach, nestNote: true);
        await SettleAsync(host);
        var reconciles = ReconcileCount(host);
        var issue = new ValidationIssue("Code", "Code A1 is taken");

        await host.InvokeAsync(() =>
        {
            host.FindComponent<NestedNote>().Instance.Hide();
            if (defaultMark)
            {
                Assert.True(Apply(host, attach ? "validator" : "form", default, issue));
            }
            else if (attach)
            {
                host.FindComponent<FormidableValidator<Reservation>>().Instance.ApplyServerIssues([issue]);
            }
            else
            {
                host.FindComponent<FormidableForm<Reservation>>().Instance.ApplyServerIssues([issue]);
            }

            Assert.Equal(reconciles, ReconcileCount(host));
        });
        Assert.Equal(["Code A1 is taken"], Messages(Engine(host), FieldIdentifier.Create(() => model.Code)));
        await SettleAsync(host);

        Assert.Empty(Messages(Engine(host), FieldIdentifier.Create(() => model.Code)));
        await Services.DisposeAsync();
    }

    // Taking a rendered field change in can rebuild the form's messages (here Note had a live
    // answer filed when it left), which raises OnValidationStateChanged and StateChanged, so a
    // throw from a handler of either comes out of MarkServerRequest itself.
    // Mutation that must break it: take nothing in at the mark, and nothing throws there.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkServerRequest_surfaces_a_handler_throw_from_the_change_it_takes_in(bool attach)
    {
        var model = new Reservation();
        var host = RenderHost(model, new ReservationRules(), Options("default"), attach, nestNote: true);
        await SettleAsync(host);
        await host.InvokeAsync(() => host.Find("[data-entry=\"Note\"] input").Change("noted"));
        await SettleAsync(host);

        var engine = Engine(host);
        var thrown = new InvalidOperationException("from the page's handler");
        void Throw(object? sender, FormidableStateChangedEventArgs e) => throw thrown;
        engine.StateChanged += Throw;
        var caught = await host.InvokeAsync(() =>
        {
            host.FindComponent<NestedNote>().Instance.Hide();
            return Record.Exception(() => MarkThrough(host, attach));
        });
        engine.StateChanged -= Throw;

        Assert.Same(thrown, caught);
        await Services.DisposeAsync();
    }

    // A value an input commits on blur reaches the model before the form hears of it. The form
    // counts the edit when the blur announces it, so a mark taken between the two is older than
    // the edit, and the reply is dropped although the request carried the new value.
    [Fact]
    public async Task A_value_held_until_blur_counts_as_an_edit_when_the_blur_announces_it()
    {
        var model = new Reservation();
        var cut = RenderHost(model, new ReservationRules(), Options("judged"), attach: false, blurCommitsCode: true);
        await SettleAsync(cut);
        var form = cut.FindComponent<FormidableForm<Reservation>>().Instance;

        await cut.InvokeAsync(() => cut.Find("[data-entry=\"Code\"] input").Change("B2"));
        Assert.Equal("B2", model.Code);
        var sentAt = await cut.InvokeAsync(() => form.MarkServerRequest());
        await cut.InvokeAsync(() => cut.Find("[data-entry=\"Code\"] input").Blur());
        await SettleAsync(cut);

        Assert.False(await cut.InvokeAsync(() => form.ApplyServerIssues([new ValidationIssue("Code", "Code B2 is taken")], sentAt)));
        await Services.DisposeAsync();
    }

    // ================================================================ helpers ====

    private static FormidableOptions Options(string config) => config switch
    {
        "judged" => new FormidableOptions { LiveProfile = ValidationProfile.Draft },
        "default" => new FormidableOptions(),
        _ => throw new ArgumentOutOfRangeException(nameof(config), config, null),
    };

    private IRenderedComponent<ReservationHost> RenderHost(Reservation model, ReservationRules rules, FormidableOptions options, bool attach, bool showNote = false, bool blurCommitsCode = false, bool nestNote = false) =>
        Render<ReservationHost>(p => p
            .Add(h => h.Model, model)
            .Add(h => h.BlurCommitsCode, blurCommitsCode)
            .Add(h => h.NestNote, nestNote)
            .Add(h => h.Validator, new FluentValidationModelValidator<Reservation>(rules))
            .Add(h => h.Options, options)
            .Add(h => h.Attach, attach)
            .Add(h => h.ShowNote, showNote)
            .Add(h => h.AttachContext, attach ? new EditContext(model) : null));

    private static IFormidableEngine Engine(IRenderedComponent<ReservationHost> cut) =>
        cut.Instance.Attach
            ? cut.FindComponent<FormidableValidator<Reservation>>().Instance.Engine!
            : cut.FindComponent<FormidableForm<Reservation>>().Instance.Engine!;

    private static ServerRequestMark MarkThrough(IRenderedComponent<ReservationHost> cut, bool attach) =>
        attach
            ? cut.FindComponent<FormidableValidator<Reservation>>().Instance.MarkServerRequest()
            : cut.FindComponent<FormidableForm<Reservation>>().Instance.MarkServerRequest();

    private static int ReconcileCount(IRenderedComponent<ReservationHost> cut) =>
        cut.Instance.Attach
            ? cut.FindComponent<FormidableValidator<Reservation>>().Instance.ReconcileCount
            : cut.FindComponent<FormidableForm<Reservation>>().Instance.ReconcileCount;

    private static Task<ServerRequestMark> MarkAsync(IRenderedComponent<ReservationHost> cut, string route) =>
        cut.InvokeAsync(() => route switch
        {
            "engine" => Engine(cut).MarkServerRequest(),
            "validator" or "validator-problem" => cut.FindComponent<FormidableValidator<Reservation>>().Instance.MarkServerRequest(),
            _ => cut.FindComponent<FormidableForm<Reservation>>().Instance.MarkServerRequest(),
        });

    // Called inside the test's own InvokeAsync, so the apply and anything read beside it run as
    // one piece of work on the dispatcher.
    private static bool Apply(IRenderedComponent<ReservationHost> cut, string route, ServerRequestMark sentAt, params ValidationIssue[] issues) => route switch
    {
        "engine" => Engine(cut).ApplyServerIssues(issues, sentAt),
        "validator" => cut.FindComponent<FormidableValidator<Reservation>>().Instance.ApplyServerIssues(issues, sentAt),
        "validator-problem" => cut.FindComponent<FormidableValidator<Reservation>>().Instance.ApplyServerIssues(Problem(issues), sentAt),
        "form-problem" => cut.FindComponent<FormidableForm<Reservation>>().Instance.ApplyServerIssues(Problem(issues), sentAt),
        _ => cut.FindComponent<FormidableForm<Reservation>>().Instance.ApplyServerIssues(issues, sentAt),
    };

    private static void ApplyUnmarked(IRenderedComponent<ReservationHost> cut, string route, params ValidationIssue[] issues)
    {
        switch (route)
        {
            case "engine":
                Engine(cut).ApplyServerIssues(issues);
                break;
            case "validator":
                cut.FindComponent<FormidableValidator<Reservation>>().Instance.ApplyServerIssues(issues);
                break;
            case "validator-problem":
                cut.FindComponent<FormidableValidator<Reservation>>().Instance.ApplyServerIssues(Problem(issues));
                break;
            case "form-problem":
                cut.FindComponent<FormidableForm<Reservation>>().Instance.ApplyServerIssues(Problem(issues));
                break;
            default:
                cut.FindComponent<FormidableForm<Reservation>>().Instance.ApplyServerIssues(issues);
                break;
        }
    }

    private static FormidableValidationProblem Problem(IEnumerable<ValidationIssue> issues)
    {
        var problem = new FormidableValidationProblem();
        foreach (var group in issues.GroupBy(i => i.Path))
        {
            problem.Errors[group.Key] = [.. group.Select(i => i.Message)];
        }

        return problem;
    }

    private static Task EditAsync(IRenderedComponent<ReservationHost> cut, string entry, string value) =>
        cut.InvokeAsync(() => cut.Find($"[data-entry=\"{entry}\"] input").Change(value));

    // Moves the clock past RefreshDebounce, waits for every check to land, then lets a posted
    // reconcile or republish run: the dispatcher runs one piece of work at a time, so the queued
    // no-op completes after them.
    private async Task SettleAsync(IRenderedComponent<ReservationHost> cut)
    {
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => _clock.Advance(PastRefresh));
        cut.WaitForState(() => !Engine(cut).IsValidating);
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => { });
    }

    private static List<string> Messages(IFormidableEngine engine, FieldIdentifier field) =>
        [.. engine.GetIssues(field).Select(i => i.Message)];

    private static List<string> Describe(IReadOnlyList<VisibleIssue> issues) =>
        [.. issues.Select(v => $"{v.Field.FieldName}|{v.Issue.Severity}|{v.Issue.Message}")];

    private List<string> FocusFor(object model) =>
        [.. _focus.Requests.Where(f => ReferenceEquals(f.Model, model)).Select(f => f.FieldName)];

    /// <summary>Every field of the engine, written out: values by value, Formidable's own objects field by field, collections item by item (sets and dictionaries in a sorted order), and any other object by its type name, so two engines that ran the same steps read alike.</summary>
    private static string Snapshot(IFormidableEngine engine)
    {
        var text = new StringBuilder();
        DescribeFields(text, engine, depth: 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return text.ToString();
    }

    private static void DescribeFields(StringBuilder text, object owner, int depth, HashSet<object> seen)
    {
        for (var type = owner.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                text.Append(' ', depth * 2).Append(field.Name).Append('=');
                DescribeValue(text, field.GetValue(owner), depth, seen);
                text.Append('\n');
            }
        }
    }

    private static void DescribeValue(StringBuilder text, object? value, int depth, HashSet<object> seen)
    {
        switch (value)
        {
            case null:
                text.Append("null");
                return;
            case string s:
                text.Append('"').Append(s).Append('"');
                return;
            case FieldIdentifier f:
                text.Append(f.Model.GetType().Name).Append('.').Append(f.FieldName);
                return;
            case Delegate:
                text.Append("delegate");
                return;
            case IComponent:
                text.Append(value.GetType().Name);
                return;
            case IDictionary dictionary:
            {
                var entries = new List<string>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = new StringBuilder();
                    DescribeValue(key, entry.Key, depth + 1, seen);
                    key.Append(':');
                    DescribeValue(key, entry.Value, depth + 1, seen);
                    entries.Add(key.ToString());
                }

                entries.Sort(StringComparer.Ordinal);
                text.Append('{').AppendJoin(", ", entries).Append('}');
                return;
            }

            case IEnumerable items:
            {
                var described = new List<string>();
                foreach (var item in items)
                {
                    var one = new StringBuilder();
                    DescribeValue(one, item, depth + 1, seen);
                    described.Add(one.ToString());
                }

                if (value is not IList)
                {
                    described.Sort(StringComparer.Ordinal);
                }

                text.Append('[').AppendJoin(", ", described).Append(']');
                return;
            }
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is decimal)
        {
            text.Append(value);
            return;
        }

        var ours = type.Assembly == typeof(IFormidableEngine).Assembly || type.Assembly == typeof(ValidationIssue).Assembly;
        if (ours && depth < 4 && (type.IsValueType || seen.Add(value)))
        {
            text.Append(type.Name).Append(" {\n");
            DescribeFields(text, value, depth + 1, seen);
            text.Append(' ', depth * 2).Append('}');
            return;
        }

        text.Append(type.Name);
    }

    /// <summary>A name and a code, each valid as the form opens, and a note no rule reads.</summary>
    public sealed class Reservation
    {
        public string? Name { get; set; } = "Ada";

        public string? Code { get; set; } = "A1";

        public string? Note { get; set; } = "note";
    }

    /// <summary>No draft rules, as on /server; the submit rules require Name and Code, each counting its runs.</summary>
    public sealed class ReservationRules : DraftSubmitValidator<Reservation>
    {
        private int _nameRuns;
        private int _codeRuns;

        public int NameRuns => Volatile.Read(ref _nameRuns);

        public int CodeRuns => Volatile.Read(ref _codeRuns);

        public int Runs => NameRuns + CodeRuns;

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(m => m.Name).Must(value =>
            {
                Interlocked.Increment(ref _nameRuns);
                return !string.IsNullOrEmpty(value);
            }).WithMessage(NameRequired);
            RuleFor(m => m.Code).Must(value =>
            {
                Interlocked.Increment(ref _codeRuns);
                return !string.IsNullOrEmpty(value);
            }).WithMessage(CodeRequired);
        }
    }

    /// <summary>A form over <see cref="Reservation"/>, on either root: kit inputs for Name and Code, one for Note while <see cref="ShowNote"/> holds, and a caption a render can change without moving a field. Attach mode renders an <see cref="EditForm"/> over <see cref="AttachContext"/>.</summary>
    private sealed class ReservationHost : ComponentBase
    {
        [Parameter]
        public Reservation Model { get; set; } = default!;

        [Parameter]
        public IModelValidator<Reservation> Validator { get; set; } = default!;

        [Parameter]
        public FormidableOptions Options { get; set; } = default!;

        [Parameter]
        public bool Attach { get; set; }

        [Parameter]
        public bool ShowNote { get; set; }

        [Parameter]
        public bool Hidden { get; set; }

        [Parameter]
        public EditContext? AttachContext { get; set; }

        [Parameter]
        public string Caption { get; set; } = string.Empty;

        [Parameter]
        public bool BlurCommitsCode { get; set; }

        [Parameter]
        public bool NestNote { get; set; }

        [Parameter]
        public bool AnchorCode { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.AddMarkupContent(0, $"<p class=\"caption\">{Caption}</p>");
            if (Hidden)
            {
                return;
            }

            if (Attach)
            {
                builder.OpenComponent<EditForm>(1);
                builder.AddComponentParameter(2, nameof(EditForm.EditContext), AttachContext);
                builder.AddComponentParameter(3, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableValidator<Reservation>>(0);
                    inner.AddComponentParameter(1, nameof(FormidableValidator<Reservation>.Validator), Validator);
                    inner.AddComponentParameter(2, nameof(FormidableValidator<Reservation>.Options), Options);
                    inner.AddComponentParameter(3, nameof(FormidableValidator<Reservation>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Fields));
                    inner.CloseComponent();
                }));
                builder.CloseComponent();
                return;
            }

            builder.OpenComponent<FormidableForm<Reservation>>(10);
            builder.AddComponentParameter(11, nameof(FormidableForm<Reservation>.Model), Model);
            builder.AddComponentParameter(12, nameof(FormidableForm<Reservation>.Validator), Validator);
            builder.AddComponentParameter(13, nameof(FormidableForm<Reservation>.Options), Options);
            builder.AddComponentParameter(14, nameof(FormidableForm<Reservation>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Fields));
            builder.CloseComponent();
        }

        private void Fields(RenderTreeBuilder builder)
        {
            KitField(builder, 10, "Name", () => Model.Name, v => Model.Name = v);
            KitField(builder, 30, "Code", () => Model.Code, v => Model.Code = v, BlurCommitsCode ? InputUpdateMode.OnBlur : InputUpdateMode.OnChange);
            if (ShowNote)
            {
                KitField(builder, 50, "Note", () => Model.Note, v => Model.Note = v);
            }

            if (NestNote)
            {
                builder.OpenComponent<NestedNote>(70);
                builder.AddComponentParameter(71, nameof(NestedNote.Model), Model);
                builder.CloseComponent();
            }

            if (AnchorCode)
            {
                builder.OpenComponent<FormidableFieldAnchor<string?>>(80);
                builder.AddComponentParameter(81, "For", (Expression<Func<string?>>)(() => Model.Code));
                builder.CloseComponent();
            }
        }

        private void KitField(RenderTreeBuilder builder, int sequence, string entry, Expression<Func<string?>> field, Action<string?> set, InputUpdateMode updateOn = InputUpdateMode.OnChange)
        {
            builder.OpenElement(sequence, "div");
            builder.AddAttribute(sequence + 1, "data-entry", entry);
            builder.OpenComponent<FormidableInputText>(sequence + 2);
            builder.AddComponentParameter(sequence + 3, "For", field);
            builder.AddComponentParameter(sequence + 4, "Value", field.Compile()());
            builder.AddComponentParameter(sequence + 5, "ValueChanged", EventCallback.Factory.Create<string?>(this, set));
            builder.AddComponentParameter(sequence + 6, nameof(FormidableInputText.UpdateOn), updateOn);
            builder.CloseComponent();
            builder.OpenComponent<FormidableFieldMessage<string?>>(sequence + 7);
            builder.AddComponentParameter(sequence + 8, "For", field);
            builder.CloseComponent();
            builder.CloseElement();
        }
    }

    /// <summary>A kit input for Note inside its own component, which can stop rendering it with a render of its own that leaves the form's render out.</summary>
    private sealed class NestedNote : ComponentBase
    {
        private bool _shown = true;

        [Parameter]
        public Reservation Model { get; set; } = default!;

        /// <summary>Stops rendering Note; called on the dispatcher, the render runs at once.</summary>
        public void Hide()
        {
            _shown = false;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (!_shown)
            {
                return;
            }

            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "data-entry", "Note");
            builder.OpenComponent<FormidableInputText>(2);
            builder.AddComponentParameter(3, "For", (Expression<Func<string?>>)(() => Model.Note));
            builder.AddComponentParameter(4, "Value", Model.Note);
            builder.AddComponentParameter(5, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => Model.Note = v));
            builder.CloseComponent();
            builder.CloseElement();
        }
    }

    /// <summary>One order line.</summary>
    public sealed class OrderLine
    {
        public string Sku { get; set; } = string.Empty;
    }

    /// <summary>An order's lines.</summary>
    public sealed class Order
    {
        public List<OrderLine> Lines { get; } = [new OrderLine(), new OrderLine()];
    }

    /// <summary>Every line's SKU required, with no draft rules, as on /server.</summary>
    public sealed class OrderRules : DraftSubmitValidator<Order>
    {
        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules() =>
            RuleForEach(p => p.Lines).ChildRules(line => line.RuleFor(l => l.Sku).NotEmpty().WithMessage("SKU is required"));
    }

    /// <summary>
    /// A page shaped like /server's Send, on either root: two lines, and a button whose handler
    /// drops every line whose SKU holds only spaces, takes a mark (after one yield when
    /// <see cref="YieldBeforeMark"/> holds), waits for the test to hand it the reply, and applies
    /// the reply with the mark. With <see cref="MarkOnFirstRender"/> it also takes a mark in its
    /// own first OnAfterRender.
    /// </summary>
    public sealed class SendingPage : ComponentBase
    {
        private readonly FormidableOptions _options = new() { LiveProfile = ValidationProfile.Draft };
        private readonly IModelValidator<Order> _validator = new FluentValidationModelValidator<Order>(new OrderRules());
        private EditContext? _editContext;

        [Parameter]
        public bool YieldBeforeMark { get; set; }

        [Parameter]
        public bool Attach { get; set; }

        [Parameter]
        public bool MarkOnFirstRender { get; set; }

        public Order Model { get; } = new();

        public List<OrderLine> Lines => Model.Lines;

        public FormidableForm<Order>? Form { get; private set; }

        public FormidableValidator<Order>? Validator { get; private set; }

        public IFormidableEngine? Engine => Attach ? Validator?.Engine : Form?.Engine;

        public TaskCompletionSource<IReadOnlyList<ValidationIssue>>? Reply { get; private set; }

        public bool? Applied { get; private set; }

        public ServerRequestMark? FirstMark { get; private set; }

        public ServerRequestMark Mark() => Attach ? Validator!.MarkServerRequest() : Form!.MarkServerRequest();

        public bool Apply(IEnumerable<ValidationIssue> issues, ServerRequestMark sentAt) =>
            Attach ? Validator!.ApplyServerIssues(issues, sentAt) : Form!.ApplyServerIssues(issues, sentAt);

        protected override void OnInitialized() => _editContext = new EditContext(Model);

        protected override void OnAfterRender(bool firstRender)
        {
            if (firstRender && MarkOnFirstRender)
            {
                FirstMark = Mark();
            }
        }

        private async Task Send()
        {
            Lines.RemoveAll(line => line.Sku.Length > 0 && string.IsNullOrWhiteSpace(line.Sku));
            if (YieldBeforeMark)
            {
                await Task.Yield();
            }

            var sentAt = Mark();
            Reply = new TaskCompletionSource<IReadOnlyList<ValidationIssue>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var issues = await Reply.Task;
            Applied = Apply(issues, sentAt);
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Attach)
            {
                builder.OpenComponent<EditForm>(10);
                builder.AddComponentParameter(11, nameof(EditForm.EditContext), _editContext);
                builder.AddComponentParameter(12, nameof(EditForm.ChildContent), (RenderFragment<EditContext>)(_ => inner =>
                {
                    inner.OpenComponent<FormidableValidator<Order>>(0);
                    inner.AddComponentParameter(1, nameof(FormidableValidator<Order>.Validator), _validator);
                    inner.AddComponentParameter(2, nameof(FormidableValidator<Order>.Options), _options);
                    inner.AddComponentParameter(3, nameof(FormidableValidator<Order>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Content));
                    inner.AddComponentReferenceCapture(4, r => Validator = (FormidableValidator<Order>)r);
                    inner.CloseComponent();
                }));
                builder.CloseComponent();
                return;
            }

            builder.OpenComponent<FormidableForm<Order>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<Order>.Model), Model);
            builder.AddComponentParameter(2, nameof(FormidableForm<Order>.Validator), _validator);
            builder.AddComponentParameter(3, nameof(FormidableForm<Order>.Options), _options);
            builder.AddComponentParameter(4, nameof(FormidableForm<Order>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => Content));
            builder.AddComponentReferenceCapture(5, r => Form = (FormidableForm<Order>)r);
            builder.CloseComponent();
        }

        private void Content(RenderTreeBuilder inner)
        {
            for (var i = 0; i < Lines.Count; i++)
            {
                var line = Lines[i];
                inner.OpenElement(0, "div");
                inner.SetKey(line);
                inner.AddAttribute(1, "data-entry", $"Sku{i}");
                inner.OpenComponent<FormidableInputText>(2);
                inner.AddComponentParameter(3, "For", (Expression<Func<string?>>)(() => line.Sku));
                inner.AddComponentParameter(4, "Value", line.Sku);
                inner.AddComponentParameter(5, "ValueChanged", EventCallback.Factory.Create<string?>(this, v => line.Sku = v ?? string.Empty));
                inner.CloseComponent();
                inner.CloseElement();
            }

            inner.OpenElement(10, "button");
            inner.AddAttribute(11, "type", "button");
            inner.AddAttribute(12, "onclick", EventCallback.Factory.Create(this, Send));
            inner.CloseElement();
        }
    }

    /// <summary>An engine written before the mark existed: it forwards every member it knows to a real engine and implements neither new one.</summary>
    private sealed class EngineWithoutTheMark(IFormidableEngine inner) : IFormidableEngine
    {
        public EditContext EditContext => inner.EditContext;

        public FieldRegistry Registry => inner.Registry;

        public FormidableOptions Options => inner.Options;

        public bool IsValidating => inner.IsValidating;

        public bool HasSubmitted => inner.HasSubmitted;

        public bool IsFormValid => inner.IsFormValid;

        public event EventHandler<FormidableStateChangedEventArgs>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public event EventHandler<FormidableValidationFaultedEventArgs>? ValidationFaulted
        {
            add => inner.ValidationFaulted += value;
            remove => inner.ValidationFaulted -= value;
        }

        public FieldState GetFieldState(FieldIdentifier field) => inner.GetFieldState(field);

        public FieldRequirement GetFieldRequirement(FieldIdentifier field) => inner.GetFieldRequirement(field);

        public IReadOnlyList<ValidationIssue> GetIssues(FieldIdentifier field) => inner.GetIssues(field);

        public IReadOnlyList<VisibleIssue> GetVisibleIssues() => inner.GetVisibleIssues();

        public void MarkTouched(FieldIdentifier field) => inner.MarkTouched(field);

        public Task<SubmitOutcome> ValidateForSubmitAsync(CancellationToken cancellationToken = default) =>
            inner.ValidateForSubmitAsync(cancellationToken);

        public Task DiscloseLoadedValuesAsync(CancellationToken cancellationToken = default) =>
            inner.DiscloseLoadedValuesAsync(cancellationToken);

        public void ApplyServerIssues(IEnumerable<ValidationIssue> issues) => inner.ApplyServerIssues(issues);
    }
}
