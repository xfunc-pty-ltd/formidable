using System.Linq.Expressions;
using Bunit;
using FluentValidation;
using Formidable.Blazor.Tests.Fixtures;
using Formidable.Introspection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Formidable.Blazor.Tests;

/// <summary>
/// A server reply shows no client answer older than the latest edit. When an edit came after the
/// form last checked every rule, what that check found stays off a field the reply reveals, which
/// shows the server's answer and any message from its own live check. The reply starts a
/// whole-form re-check, at once or as a validity check already running lands, and once a re-check
/// of the values as they stand lands, its answer joins the server's. A reply whose stored client
/// answer already saw every edit changes nothing, and no rule runs for it.
/// </summary>
/// <remarks>
/// The clock is a <see cref="FakeTimeProvider"/> behind <see cref="ZeroWaitClock"/>, which holds a
/// timer armed at zero until a test runs it, so a zero wait runs after the code that armed it has
/// returned. So what a test reads right after a reply is what the reply itself did, and the
/// re-check it starts runs only when the test says so. Every async rule counts each run as it
/// starts, and every sync rule set that matters here counts its runs too.
/// </remarks>
public class ServerApplyStaleClientTests : BunitContext
{
    private const string CodeRequired = "Code required";
    private const string CodeTooShort = "Code too short";
    private const string CodeTaken = "Code taken";
    private const string Valid = "formidable-valid";

    private static readonly TimeSpan PastRefresh = new FormidableOptions().RefreshDebounce + TimeSpan.FromMilliseconds(1);
    private static readonly string GateText = new FormidableOptions().DefensiveGateMessage;

    private readonly FakeTimeProvider _time = new();
    private readonly ZeroWaitClock _clock;

    public ServerApplyStaleClientTests()
    {
        _clock = new ZeroWaitClock(_time);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddFormidableBlazor();
        Services.AddSingleton<TimeProvider>(_clock);
    }

    // ================================================================ who reaches it ====

    // Code opens empty, so the form's opening check answers for "" with "Code required". The
    // visitor types "ab", and the reply is the server's answer for "ab": "Code too short". Wherever
    // the stored answer predates the edit, Code shows the reply alone at once, and the client's own
    // answer for "ab" joins it once the re-check the reply starts lands (the same text here, so one
    // message shows). "Code required" describes the empty value and never shows. editRuns counts
    // the runs of Code's submit rules the edit itself started, and applyRuns those from the reply
    // on. The re-check runs them once where nothing had, and finds them answered where a check the
    // edit started already ran them.
    // Mutations: let the views ignore what the reply held back, and every row shows "Code required"
    // between the reply and its re-check; never start the re-check, and the Draft rows and the row
    // whose window never closes run nothing for the reply (the 400 ms window's own check runs the
    // rules once, the same total).
    [Theory]
    [InlineData("draft", 0, 1)]
    [InlineData("draft+tracking", 1, 0)]
    [InlineData("submit-equal-copy", 1, 0)]
    [InlineData("wider-than-submit", 1, 0)]
    [InlineData("window-never-closes", 0, 1)]
    [InlineData("window-400ms", 0, 1)]
    [InlineData("async-held-draft", 0, 1)]
    [InlineData("async-held-default", 1, 0)]
    public async Task A_reply_shows_no_answer_older_than_an_edit(string config, int editRuns, int applyRuns)
    {
        var model = new Ticket();
        var isAsync = config.StartsWith("async", StringComparison.Ordinal);
        var rules = new TicketRules();
        var asyncRules = new AsyncTicketRules();
        IModelValidator<Ticket> validator = isAsync
            ? new FluentValidationModelValidator<Ticket>(asyncRules)
            : new FluentValidationModelValidator<Ticket>(rules);
        int Runs() => isAsync ? asyncRules.Gate.Runs : rules.Runs;

        var cut = RenderTicket(model, validator, TicketOptions(config));
        var engine = EngineOf(cut);
        var code = CodeOf(model);
        await SettleAsync(cut, engine);
        var atOpen = Runs();

        asyncRules.Gate.Hold = isAsync;
        await EditAsync(cut, "Code", "ab");
        Assert.Equal(editRuns, Runs() - atOpen);
        var afterEdit = Runs();

        await ApplyAsync(cut, new ValidationIssue(nameof(Ticket.Code), CodeTooShort));
        Assert.Equal([CodeTooShort], Messages(engine, code));
        if (config == "async-held-default")
        {
            Assert.True(engine.GetFieldState(code).IsValidating); // the edit's own check is still out
        }

        await cut.InvokeAsync(() => _clock.RunZeroWaits());
        Assert.Equal([CodeTooShort], Messages(engine, code));

        asyncRules.Gate.Release();
        await SettleAsync(cut, engine);
        await SettleAsync(cut, engine, TimeSpan.FromMilliseconds(500)); // past a 400 ms window
        Assert.Equal([CodeTooShort], Messages(engine, code));
        Assert.Equal(applyRuns, Runs() - afterEdit);
        Assert.False(engine.GetFieldState(code).IsValidating);

        await Services.DisposeAsync();
    }

    // Controls. With the defaults, with or without tracking, and with LiveProfile set to the submit
    // profile's own instance, the edit's check answers the submit rules for "ab", so the reply finds
    // nothing to hold back and starts nothing. With a RefreshDebounce that never passes no re-check
    // can run, so the reply starts none, and Code keeps showing the server's answer alone.
    [Theory]
    [InlineData("default", 1)]
    [InlineData("default+tracking", 1)]
    [InlineData("submit-same-instance", 1)]
    [InlineData("draft+recheck-off", 0)]
    public async Task A_reply_starts_nothing_where_the_answer_saw_the_edit_or_no_recheck_can_run(string config, int editRuns)
    {
        var model = new Ticket();
        var rules = new TicketRules();
        var cut = RenderTicket(model, new FluentValidationModelValidator<Ticket>(rules), TicketOptions(config));
        var engine = EngineOf(cut);
        var code = CodeOf(model);
        await SettleAsync(cut, engine);
        var atOpen = rules.Runs;

        await EditAsync(cut, "Code", "ab");
        Assert.Equal(editRuns, rules.Runs - atOpen);

        await ApplyAsync(cut, new ValidationIssue(nameof(Ticket.Code), CodeTooShort));
        Assert.Equal([CodeTooShort], Messages(engine, code));
        Assert.Equal(0, _clock.ZeroWaits);

        await SettleAsync(cut, engine);
        Assert.Equal([CodeTooShort], Messages(engine, code));
        Assert.Equal(editRuns, rules.Runs - atOpen);

        await Services.DisposeAsync();
    }

    // The finding's own steps on a form the server judges. Right after the reply, Code shows the
    // server's issue itself. Once the re-check the reply started lands, the client's own copy of the
    // same message shows in its place, because the views show the client's copy first and drop the
    // server's identical one. An empty reply leaves the client's answer showing, and the next edit's
    // re-check clears it.
    // Mutation: let the views ignore what the reply held back, and "Code required" shows right
    // after the reply.
    [Fact]
    public async Task A_reply_after_an_unanswered_edit_shows_only_the_servers_answer_then_the_clients_current_one()
    {
        var model = new Ticket();
        var rules = new TicketRules();
        var cut = RenderTicket(model, new FluentValidationModelValidator<Ticket>(rules), TicketOptions("draft"));
        var engine = EngineOf(cut);
        var code = CodeOf(model);
        await SettleAsync(cut, engine);
        await EditAsync(cut, "Code", "ab");
        await SettleAsync(cut, engine); // before a submit or reply, nothing re-checks an edit
        var runs = rules.Runs;

        var reply = new ValidationIssue(nameof(Ticket.Code), CodeTooShort);
        await ApplyAsync(cut, reply);
        Assert.Same(reply, Assert.Single(engine.GetIssues(code)));
        Assert.Equal(runs, rules.Runs);

        await cut.InvokeAsync(() => _clock.RunZeroWaits()); // the re-check the reply started
        var current = Assert.Single(engine.GetIssues(code));
        Assert.NotSame(reply, current);
        Assert.Equal(CodeTooShort, current.Message);
        Assert.Equal(runs + 1, rules.Runs);

        await ApplyAsync(cut);
        Assert.Equal([CodeTooShort], Messages(engine, code));

        await EditAsync(cut, "Code", "abc");
        await SettleAsync(cut, engine);
        Assert.Empty(Messages(engine, code));
        Assert.DoesNotContain(CodeRequired, Messages(engine, code));

        await Services.DisposeAsync();
    }

    // ================================================================ the /server page ====

    // The /server page's steps, with its validator rule for rule and its options. Type a
    // description with a hyphen and leave it, click Add line (which re-checks the whole form), clear
    // the description and leave it, then apply the server's reply. The re-check after Add line
    // answered for "Q3-restock", whose hyphen draws a warning, so the description the reply reveals
    // must not show that warning while it is empty. Without Add line the stored answer is the one
    // for the empty description the form opened with, and with the defaults each edit's own check
    // keeps it current; neither has a warning to hold back.
    // Mutation: let the views ignore what the reply held back, and the page's own row shows the
    // warning right after the reply, under the field and in the visible issues.
    [Theory]
    [InlineData("draft", true)]
    [InlineData("draft", false)]
    [InlineData("default", true)]
    public async Task On_the_server_page_an_emptied_description_shows_no_hyphen_warning(string config, bool addLine)
    {
        var options = config == "draft" ? new FormidableOptions { LiveProfile = ValidationProfile.Draft } : new FormidableOptions();
        var cut = Render<OrderHost>(p => p.Add(h => h.Options, options));
        var host = cut.Instance;
        var form = host.Form!;
        var engine = form.Engine!;
        var description = FieldIdentifier.Create(() => host.Model.Description);

        await SettleAsync(cut, engine);
        await EditAsync(cut, "Description", "Q3-restock");
        await SettleAsync(cut, engine);
        if (addLine)
        {
            await cut.InvokeAsync(host.AddLine);
            await SettleAsync(cut, engine);
        }

        await EditAsync(cut, "Description", string.Empty);
        await SettleAsync(cut, engine);

        var reply = new List<ValidationIssue> { new(nameof(Order.Description), "Description is required") };
        for (var i = 0; i < host.Model.Lines.Count; i++)
        {
            reply.Add(new ValidationIssue($"Lines[{i}].Sku", "SKU is required"));
        }

        await cut.InvokeAsync(() => form.ApplyServerIssues(reply));
        AssertNoHyphenWarning(cut, engine, description);
        Assert.Equal(["Description is required"], Messages(engine, description));

        await SettleAsync(cut, engine);
        AssertNoHyphenWarning(cut, engine, description);
        Assert.Equal(["Description is required"], Messages(engine, description));

        await Services.DisposeAsync();
    }

    private static void AssertNoHyphenWarning(IRenderedComponent<OrderHost> cut, IFormidableEngine engine, FieldIdentifier description)
    {
        Assert.DoesNotContain(engine.GetIssues(description), i => i.Message == OrderValidator.HyphenWarning);
        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Issue.Message == OrderValidator.HyphenWarning);
        Assert.DoesNotContain("Hyphens", cut.Find("[data-entry=\"Description\"]").TextContent, StringComparison.Ordinal);
    }

    // ================================================================ a cancelled submit ====

    // The reply's re-check, or the edit's own check, is out on the async rule when the page starts a
    // submit and then cancels it. The submit displaces the check, and its end without an answer gives
    // that check back, which lands the client's answer for "ab". Code shows the server's answer alone
    // until then, and never "Code required". The rule starts three times either way: the displaced
    // check, the submit, and the check given back.
    [Theory]
    [InlineData("draft")]
    [InlineData("default")]
    public async Task A_cancelled_submit_that_displaced_the_check_leaves_nothing_stale(string config)
    {
        var model = new Ticket();
        var rules = new AsyncTicketRules();
        var cut = RenderTicket(model, new FluentValidationModelValidator<Ticket>(rules), TicketOptions(config));
        var engine = EngineOf(cut);
        var code = CodeOf(model);
        await SettleAsync(cut, engine);
        var atOpen = rules.Gate.Runs;

        rules.Gate.Hold = true;
        await EditAsync(cut, "Code", "ab");
        await ApplyAsync(cut, new ValidationIssue(nameof(Ticket.Code), CodeTooShort));
        await cut.InvokeAsync(() => _clock.RunZeroWaits());
        Assert.True(engine.IsValidating);
        Assert.Equal([CodeTooShort], Messages(engine, code));

        using var cancel = new CancellationTokenSource();
        var submit = cut.InvokeAsync(() => engine.ValidateForSubmitAsync(cancel.Token));
        Assert.True(await Until(() => rules.Gate.Runs == atOpen + 2));
        Assert.Equal([CodeTooShort], Messages(engine, code));

        cancel.Cancel();
        rules.Gate.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        Assert.Equal([CodeTooShort], Messages(engine, code));

        for (var i = 0; i < 3; i++)
        {
            await SettleAsync(cut, engine);
        }

        Assert.Equal([CodeTooShort], Messages(engine, code));
        Assert.Equal(3, rules.Gate.Runs - atOpen);
        Assert.False(engine.IsValidating);

        await Services.DisposeAsync();
    }

    // ================================================================ beside the form-level message ====

    // Name renders and Code, which nothing renders, never does. Name opens empty, so the form's
    // opening check says "Name required"; the visitor types "Ada"; then the reply names hidden Code,
    // and in two rows Name as well, with a rule only the server runs. Name never shows "Name
    // required". The form-level message shows exactly when the reply names nothing on screen.
    [Theory]
    [InlineData("draft", true)]
    [InlineData("default", true)]
    [InlineData("draft", false)]
    [InlineData("default", false)]
    public async Task A_reply_beside_the_form_level_message_shows_nothing_stale(string config, bool replyNamesName)
    {
        var model = new Account();
        var rules = new AccountRules();
        var options = config == "draft" ? new FormidableOptions { LiveProfile = ValidationProfile.Draft } : new FormidableOptions();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Account>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<Account>.Model), model);
            builder.AddComponentParameter(2, nameof(FormidableForm<Account>.Validator), (IModelValidator<Account>)new FluentValidationModelValidator<Account>(rules));
            builder.AddComponentParameter(3, nameof(FormidableForm<Account>.Options), options);
            builder.AddComponentParameter(4, nameof(FormidableForm<Account>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                AddInput(inner, 0, "Name", this, () => model.Name, v => model.Name = v ?? string.Empty);
                inner.OpenComponent<FormidableModelMessage>(1);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<Account>>().Instance;
        var engine = form.Engine!;
        var name = FieldIdentifier.Create(() => model.Name);
        var modelLevel = new FieldIdentifier(model, string.Empty);
        List<string> expected = replyNamesName ? ["Name taken"] : [];

        await SettleAsync(cut, engine);
        await EditAsync(cut, "Name", "Ada");
        await SettleAsync(cut, engine);

        var reply = new List<ValidationIssue> { new(nameof(Account.Code), CodeRequired) };
        if (replyNamesName)
        {
            reply.Insert(0, new ValidationIssue(nameof(Account.Name), "Name taken"));
        }

        await cut.InvokeAsync(() => form.ApplyServerIssues(reply));
        Assert.Equal(expected, Messages(engine, name));
        Assert.Equal(!replyNamesName, Messages(engine, modelLevel).Contains(GateText));

        await SettleAsync(cut, engine);
        Assert.Equal(expected, Messages(engine, name));
        Assert.Equal(!replyNamesName, Messages(engine, modelLevel).Contains(GateText));
        Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Issue.Message == "Name required");

        await Services.DisposeAsync();
    }

    // ================================================================ rule runs and green ====

    // A window that never closes runs no live check, so before a submit or reply only the validity
    // check answers an edit, and the reply holds Code back and starts the re-check. That re-check
    // answers the whole form, so the validity check the edit armed stands down for it, and the async
    // rule runs once for the edit. Name passes every rule, and keeps its green class throughout:
    // while the re-check waits to start, while it runs, and once it lands.
    // Mutations: leave the validity check armed, and it fires while the re-check still waits on the
    // rule, so the rule runs twice; stand the check down without counting the waiting re-check as
    // on its way, and Name loses its green class until the re-check lands.
    [Fact]
    public async Task A_reply_under_a_window_that_never_closes_runs_the_async_rule_once_and_keeps_green()
    {
        var model = new Account();
        var rules = new AccountRules();
        var cut = RenderAccount(model, rules, new FormidableOptions
        {
            LiveDebounce = Timeout.InfiniteTimeSpan,
            TrackFormValidity = true,
        });
        var engine = EngineOf(cut);
        var name = FieldIdentifier.Create(() => model.Name);
        var code = FieldIdentifier.Create(() => model.Code);
        await SettleAsync(cut, engine);
        await EditAsync(cut, "Name", "Ada"); // arms the validity check
        await SettleAsync(cut, engine); // which answers the whole form
        Assert.Equal(Valid, engine.EditContext.FieldCssClass(name));

        rules.Gate.Hold = true;
        await EditAsync(cut, "Code", "ab"); // arms it again
        var runs = rules.Gate.Runs;
        Assert.Equal(Valid, engine.EditContext.FieldCssClass(name));

        await ApplyAsync(cut, new ValidationIssue(nameof(Account.Code), CodeTooShort));
        Assert.Equal([CodeTooShort], Messages(engine, code));
        Assert.Equal(Valid, engine.EditContext.FieldCssClass(name));
        Assert.Equal(runs, rules.Gate.Runs);

        await cut.InvokeAsync(() => _clock.RunZeroWaits()); // the re-check starts and waits on the rule
        Assert.Equal(runs + 1, rules.Gate.Runs);
        Assert.Equal(Valid, engine.EditContext.FieldCssClass(name));

        await cut.InvokeAsync(() => _time.Advance(PastRefresh)); // where the validity check came due
        Assert.Equal(runs + 1, rules.Gate.Runs);
        Assert.Equal(Valid, engine.EditContext.FieldCssClass(name));

        rules.Gate.Release();
        await SettleAsync(cut, engine);
        await SettleAsync(cut, engine);
        Assert.Equal(runs + 1, rules.Gate.Runs);
        Assert.Equal(Valid, engine.EditContext.FieldCssClass(name));
        Assert.Equal([CodeTooShort], Messages(engine, code));
        Assert.False(engine.IsFormValid);

        await Services.DisposeAsync();
    }

    // A validity check that the window's edit armed has fired, and its probe is out on the async
    // rule for "ab" when the reply arrives. The reply holds Code back but starts no re-check beside
    // that probe, which would run the rule a second time: it waits, and the probe's landing starts
    // the re-check, which reuses the probe's answer. The rule runs once for the edit, Code ends
    // with the client's answer beside the server's, and Name, which passes every rule, keeps its
    // green class at every step.
    // Mutation: arm the re-check without waiting for the probe, and the rule runs twice.
    [Fact]
    public async Task A_reply_while_the_validity_check_runs_waits_for_it_and_runs_the_async_rule_once()
    {
        var model = new Account();
        var rules = new AccountRules();
        var cut = RenderAccount(model, rules, new FormidableOptions
        {
            LiveDebounce = Timeout.InfiniteTimeSpan,
            TrackFormValidity = true,
        });
        var engine = EngineOf(cut);
        var name = FieldIdentifier.Create(() => model.Name);
        var code = FieldIdentifier.Create(() => model.Code);
        await SettleAsync(cut, engine);
        await EditAsync(cut, "Name", "Ada");
        await SettleAsync(cut, engine);

        rules.Gate.Hold = true;
        await EditAsync(cut, "Code", "ab");
        var runs = rules.Gate.Runs;
        await cut.InvokeAsync(() => _time.Advance(PastRefresh)); // the validity check fires
        Assert.True(await Until(() => rules.Gate.Runs == runs + 1));
        Assert.Equal(Valid, engine.EditContext.FieldCssClass(name));

        await ApplyAsync(cut, new ValidationIssue(nameof(Account.Code), CodeTaken));
        Assert.Equal([CodeTaken], Messages(engine, code));
        Assert.Equal(Valid, engine.EditContext.FieldCssClass(name));
        await cut.InvokeAsync(() => _clock.RunZeroWaits()); // nothing starts beside the probe
        Assert.Equal(runs + 1, rules.Gate.Runs);
        Assert.Equal(Valid, engine.EditContext.FieldCssClass(name));

        rules.Gate.Release();
        Assert.True(await Until(() => _clock.ZeroWaits == 1)); // the probe landed and armed it
        Assert.Equal(Valid, engine.EditContext.FieldCssClass(name));
        await SettleAsync(cut, engine);
        await SettleAsync(cut, engine);
        Assert.Equal(runs + 1, rules.Gate.Runs);
        Assert.Equal([CodeTooShort, CodeTaken], Messages(engine, code));
        Assert.Equal(Valid, engine.EditContext.FieldCssClass(name));
        Assert.False(engine.IsFormValid);

        await Services.DisposeAsync();
    }

    // With the live channel narrowed and tracking on, every edit starts a probe of the whole form
    // at once, and here it is still out on the submit-only async rule when the reply arrives. The
    // reply's re-check waits for it, as above, so the rule runs once for the edit, and Code ends
    // with the client's answer beside the server's.
    // Mutation: arm the re-check without waiting for the probe, and the rule runs twice.
    [Fact]
    public async Task A_reply_while_the_edits_own_probe_runs_waits_for_it_and_runs_the_async_rule_once()
    {
        var model = new Ticket();
        var rules = new AsyncTicketRules();
        var cut = RenderTicket(model, new FluentValidationModelValidator<Ticket>(rules), TicketOptions("draft+tracking"));
        var engine = EngineOf(cut);
        var code = CodeOf(model);
        await SettleAsync(cut, engine);

        rules.Gate.Hold = true;
        var runs = rules.Gate.Runs;
        await EditAsync(cut, "Code", "ab");
        Assert.True(await Until(() => rules.Gate.Runs == runs + 1)); // the edit's probe waits

        await ApplyAsync(cut, new ValidationIssue(nameof(Ticket.Code), CodeTaken));
        Assert.Equal([CodeTaken], Messages(engine, code));
        await cut.InvokeAsync(() => _clock.RunZeroWaits()); // nothing starts beside the probe
        Assert.Equal(runs + 1, rules.Gate.Runs);

        rules.Gate.Release();
        Assert.True(await Until(() => _clock.ZeroWaits == 1)); // the probe landed and armed it
        await SettleAsync(cut, engine);
        await SettleAsync(cut, engine);
        Assert.Equal(runs + 1, rules.Gate.Runs);
        Assert.Equal([CodeTooShort, CodeTaken], Messages(engine, code));

        await Services.DisposeAsync();
    }

    // A page's StateChanged handler throws once as the edit's own probe lands, while the reply's
    // re-check waits for that probe. The probe's exit still arms the re-check, which reuses the
    // probe's answer and lands the client's answer beside the server's, so the rule runs once.
    // The engine is built directly, as a page with no root would hold it, so the throw ends the
    // probe's background task and reaches no renderer.
    // Mutation: end the probe after the landing's notifications rather than in a finally, and the
    // throw leaves the re-check waiting, with Code showing the server's answer alone.
    [Fact]
    public async Task A_handler_that_throws_as_the_probe_lands_leaves_the_reply_s_recheck_to_run()
    {
        var model = new Ticket();
        var rules = new AsyncTicketRules();
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<Ticket>(
            model, editContext, new FluentValidationModelValidator<Ticket>(rules), new ReflectionModelIntrospector(),
            TicketOptions("draft+tracking"), _clock, EngineTestSync.OneAtATime());
        var code = CodeOf(model);
        Assert.True(await Until(() => rules.Gate.Runs == 1)); // the probe the build starts

        rules.Gate.Hold = true;
        var runs = rules.Gate.Runs;
        model.Code = "ab";
        editContext.NotifyFieldChanged(code); // its probe starts and waits on the rule
        Assert.True(await Until(() => rules.Gate.Runs == runs + 1));
        engine.ApplyServerIssues([new ValidationIssue(nameof(Ticket.Code), CodeTaken)]);
        Assert.Equal([CodeTaken], Messages(engine, code));
        Assert.Equal(0, _clock.ZeroWaits); // the re-check waits for the probe

        var thrown = 0;
        engine.StateChanged += (_, _) =>
        {
            if (Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw new InvalidOperationException("a page's handler");
            }
        };
        rules.Gate.Release();
        Assert.True(await Until(() => _clock.ZeroWaits == 1)); // the probe's exit armed it
        Assert.Equal(1, Volatile.Read(ref thrown));

        _clock.RunZeroWaits();
        Assert.True(await Until(() => !engine.IsValidating && Messages(engine, code).Count == 2));
        Assert.Equal([CodeTooShort, CodeTaken], Messages(engine, code));
        Assert.Equal(runs + 1, rules.Gate.Runs);
    }

    // The same throw at the exit of a validity check whose rule faults. A faulted check owes the
    // reply no re-check, since the re-check would run the rule that threw, but its exit still
    // ends it. So a second reply, at the same edit, starts its own re-check at once instead of
    // waiting on a check that is no longer out. The validity check is the one the timer fires,
    // because the live window never closes. The engine runs its dispatches one at a time behind a
    // lock the test also takes, so the second reply comes after the exit's dispatch is over.
    // Mutation: end the check after the exit's notifications rather than in a finally, and the
    // second reply arms nothing.
    [Fact]
    public async Task A_handler_that_throws_as_a_faulted_check_ends_leaves_a_later_reply_s_recheck_to_run()
    {
        var model = new Ticket();
        var rules = new AsyncTicketRules();
        var editContext = new EditContext(model);
        var sync = new object();
        using var engine = new FormidableEngine<Ticket>(
            model, editContext, new FluentValidationModelValidator<Ticket>(rules), new ReflectionModelIntrospector(),
            new FormidableOptions { LiveDebounce = Timeout.InfiniteTimeSpan, TrackFormValidity = true }, _clock,
            work =>
            {
                lock (sync)
                {
                    return work();
                }
            });
        var code = CodeOf(model);
        Assert.True(await Until(() => rules.Gate.Runs == 1 && !engine.IsValidating)); // the probe the build starts

        rules.Gate.Hold = true;
        var runs = rules.Gate.Runs;
        model.Code = "ab";
        editContext.NotifyFieldChanged(code); // arms the validity check
        _time.Advance(PastRefresh); // which fires and waits on the rule
        Assert.True(await Until(() => rules.Gate.Runs == runs + 1));
        lock (sync)
        {
            engine.ApplyServerIssues([new ValidationIssue(nameof(Ticket.Code), CodeTaken)]);
        }

        Assert.Equal(0, _clock.ZeroWaits); // the re-check waits for the check

        var thrown = 0;
        engine.StateChanged += (_, _) =>
        {
            if (Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw new InvalidOperationException("a page's handler");
            }
        };
        rules.Gate.Fault();
        Assert.True(await Until(() => Volatile.Read(ref thrown) == 1));
        lock (sync)
        {
            Assert.Equal(0, _clock.ZeroWaits); // a faulted check arms nothing
            engine.ApplyServerIssues([new ValidationIssue(nameof(Ticket.Code), CodeTaken)]);
        }

        Assert.Equal(1, _clock.ZeroWaits);
        _clock.RunZeroWaits();
        Assert.True(await Until(() => !engine.IsValidating && Messages(engine, code).Count == 2));
        Assert.Equal([CodeTooShort, CodeTaken], Messages(engine, code));
    }

    // A page's StateChanged handler throws as the reply itself publishes, and the page catches the
    // throw from its own call. The reply has already held Code back, so it still starts the
    // re-check, which lands the client's answer beside the server's.
    // Mutation: arm the re-check after the reply's store rebuild rather than in a finally, and
    // nothing starts: Code shows the server's answer alone.
    [Fact]
    public async Task A_handler_that_throws_as_a_reply_publishes_leaves_its_recheck_to_run()
    {
        var model = new Ticket();
        var rules = new TicketRules();
        var editContext = new EditContext(model);
        using var engine = new FormidableEngine<Ticket>(
            model, editContext, new FluentValidationModelValidator<Ticket>(rules), new ReflectionModelIntrospector(),
            TicketOptions("draft"), _clock, EngineTestSync.OneAtATime());
        var code = CodeOf(model);
        model.Code = "ab";
        editContext.NotifyFieldChanged(code); // its live check runs no rule of Code's
        Assert.True(await Until(() => !engine.IsValidating));

        var thrown = 0;
        engine.StateChanged += (_, _) =>
        {
            if (Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw new InvalidOperationException("a page's handler");
            }
        };
        Assert.Throws<InvalidOperationException>(
            () => engine.ApplyServerIssues([new ValidationIssue(nameof(Ticket.Code), CodeTaken)]));
        Assert.Equal([CodeTaken], Messages(engine, code));

        Assert.Equal(1, _clock.ZeroWaits);
        _clock.RunZeroWaits();
        Assert.True(await Until(() => !engine.IsValidating && Messages(engine, code).Count == 2));
        Assert.Equal([CodeTooShort, CodeTaken], Messages(engine, code));
    }

    // The reply's re-check begins at the reply's stamp, and the visitor types "abcd" while it waits
    // on the async rule; a LiveDebounce window keeps that edit from cancelling it. Its answer is
    // about "ab", so its landing releases nothing: Code still shows the server's answer alone,
    // never "Code too short" while it holds "abcd". The edit's own re-check then answers "abcd"
    // and replaces the reply, as an edit after a reply does.
    // Mutation: release on any answer begun no earlier than the reply, and "Code too short" shows
    // once the outdated re-check lands.
    [Fact]
    public async Task A_recheck_an_edit_outdated_releases_nothing_the_reply_held_back()
    {
        var model = new Ticket();
        var rules = new AsyncTicketRules();
        var options = new FormidableOptions
        {
            LiveProfile = ValidationProfile.Draft,
            LiveDebounce = TimeSpan.FromMilliseconds(1000),
        };
        var cut = RenderTicket(model, new FluentValidationModelValidator<Ticket>(rules), options);
        var engine = EngineOf(cut);
        var code = CodeOf(model);
        await SettleAsync(cut, engine);
        await EditAsync(cut, "Code", "ab");
        await SettleAsync(cut, engine, TimeSpan.FromMilliseconds(1100));

        rules.Gate.Hold = true;
        var runs = rules.Gate.Runs;
        await ApplyAsync(cut, new ValidationIssue(nameof(Ticket.Code), CodeTaken));
        await cut.InvokeAsync(() => _clock.RunZeroWaits());
        Assert.True(await Until(() => rules.Gate.Runs == runs + 1)); // the re-check about "ab" waits
        Assert.Equal([CodeTaken], Messages(engine, code));

        await EditAsync(cut, "Code", "abcd");
        rules.Gate.ReleaseHeldRuns(); // the re-check about "ab" lands
        cut.WaitForState(() => !engine.IsValidating);
        await cut.InvokeAsync(() => { });
        Assert.Equal("abcd", model.Code);
        Assert.Equal([CodeTaken], Messages(engine, code));
        Assert.Equal([CodeTaken], engine.EditContext.GetValidationMessages(code));

        rules.Gate.Release();
        await SettleAsync(cut, engine);
        await SettleAsync(cut, engine, TimeSpan.FromMilliseconds(1100));
        Assert.Empty(Messages(engine, code));
        Assert.Equal(2, rules.Gate.Runs - runs);

        await Services.DisposeAsync();
    }

    // ================================================================ a reply during other checks ====

    // A check given back after a submit or a load runs the submit rules, so it replaces the stored
    // answer as it lands. A reply applied while it runs holds Name back and starts no re-check of
    // its own. "spent-submit" is the give-back of a blocking submit whose restarts an edit spent:
    // the edit to Name came during its last check, whose answer is for the empty Name. The edit armed
    // the whole-form re-check, which runs once the given-back check lands. "cancelled-load" is the
    // give-back of a load a page cancelled after an edit during it, where nothing else re-checks.
    // The validator runs the whole profile in one call, so every check is one run of the async rule
    // and a re-check finds nothing to reuse.
    // Mutation: leave the running check out of what the reply waits for, and the load row starts a
    // re-check behind the given-back check, which runs the rule once more.
    [Theory]
    [InlineData("spent-submit", 2)]
    [InlineData("cancelled-load", 1)]
    public async Task A_reply_applied_while_a_given_back_check_runs_arms_no_extra_recheck(string route, int runsAfterGiveBack)
    {
        using var form = Member.Build(_clock, wholeProfile: true);
        // Third's check answers the whole form, so the stored answer says Name is required, but
        // Name is not engaged, so no live message of its own shows for the empty value.
        form.Model.Third = string.Empty; // so a submit blocks
        form.Notify(nameof(Member.Third));
        await form.SettleAsync(_time);

        int runs;
        Task pass;
        using var cancel = new CancellationTokenSource();
        form.Rules.Gate.Hold = true;
        if (route == "spent-submit")
        {
            var before = form.Rules.Gate.Runs;
            pass = form.Engine.ValidateForSubmitAsync();
            Assert.True(await Until(() => form.Rules.Gate.Runs == before + 1));
            foreach (var other in new[] { "y", "z" })
            {
                var check = form.Rules.Gate.Runs;
                form.Model.Other = other;
                form.Notify(nameof(Member.Other));
                form.Rules.Gate.ReleaseHeldRuns(); // that check's answer predates the edit
                Assert.True(await Until(() => form.Rules.Gate.Runs == check + 1));
            }

            form.Model.Name = "Ada";
            form.Notify(nameof(Member.Name)); // during the last check
            runs = form.Rules.Gate.Runs;
            form.Rules.Gate.ReleaseHeldRuns(); // the last check lands, blocked by Third
            Assert.False((await (Task<SubmitOutcome>)pass).CanProceed);
        }
        else
        {
            var before = form.Rules.Gate.Runs;
            pass = form.Engine.DiscloseLoadedValuesAsync(cancel.Token);
            Assert.True(await Until(() => form.Rules.Gate.Runs == before + 1));
            form.Model.Name = "Ada";
            form.Notify(nameof(Member.Name)); // during the load
            runs = form.Rules.Gate.Runs;
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);
        }

        Assert.True(await Until(() => form.Rules.Gate.Runs == runs + 1)); // the given-back check waits
        Assert.True(form.Engine.IsValidating);

        form.Engine.ApplyServerIssues([new ValidationIssue(nameof(Member.Name), Member.NameTaken)]);
        Assert.Equal([Member.NameTaken], form.Messages(nameof(Member.Name)));
        _clock.RunZeroWaits();

        form.Rules.Gate.Release();
        await form.SettleAsync(_time);
        await form.SettleAsync(_time);
        Assert.Equal([Member.NameTaken], form.Messages(nameof(Member.Name)));
        Assert.Equal(runsAfterGiveBack, form.Rules.Gate.Runs - runs);
    }

    // A submit an edit outdated checks again, and the check it was running writes nothing. A reply
    // applied while the second check runs holds Name back, because the stored answer is still the
    // one for the empty Name, and starts nothing, because the submit will replace that answer. Once
    // the submit lands, nothing is held back: Name shows the client's answer for "Ad" beside the
    // server's. The rule runs once per check, and the re-check the edit armed finds it answered.
    // Mutation: keep what the reply held back when the submit lands, and "Name too short" stays
    // hidden.
    [Fact]
    public async Task A_reply_applied_while_a_restarted_submit_runs_holds_back_nothing_stale()
    {
        using var form = Member.Build(_clock, wholeProfile: false, everyFieldShows: true);
        // Other's check answers the whole form, so the stored answer says Name is required, but
        // Name is not engaged, so no live message of its own shows for the empty value.
        form.Notify(nameof(Member.Other));
        await form.SettleAsync(_time);
        var before = form.Rules.Gate.Runs;

        form.Rules.Gate.Hold = true;
        var submit = form.Engine.ValidateForSubmitAsync();
        Assert.True(await Until(() => form.Rules.Gate.Runs == before + 1));
        form.Model.Name = "Ad";
        form.Notify(nameof(Member.Name)); // outdates the first check
        form.Rules.Gate.ReleaseHeldRuns();
        Assert.True(await Until(() => form.Rules.Gate.Runs == before + 2)); // the second check waits

        form.Engine.ApplyServerIssues([new ValidationIssue(nameof(Member.Name), Member.NameTaken)]);
        Assert.Equal([Member.NameTaken], form.Messages(nameof(Member.Name)));
        Assert.Equal(0, _clock.ZeroWaits);

        form.Rules.Gate.Release();
        Assert.False((await submit).CanProceed);
        Assert.Equal([Member.NameTooShort, Member.NameTaken], form.Messages(nameof(Member.Name)));

        await form.SettleAsync(_time);
        Assert.Equal([Member.NameTooShort, Member.NameTaken], form.Messages(nameof(Member.Name)));
        Assert.Equal(2, form.Rules.Gate.Runs - before);
    }

    // A reply that names Name with a warning alone arrives during a submit's last check, while the
    // stored answer is older than an edit, so it holds Name back. An edit during that check
    // outdates it after every restart, so the blocking answer lands and keeps the held-back set.
    // Name's client errors stay hidden, so the submit discloses nothing on screen: the form-level
    // message explains the block, and the outcome's summary names no hidden error. Once the checks
    // the edit owes land, Name shows its own errors and the form-level message goes; the edit's
    // re-check also replaces the reply, as a re-check after an edit does.
    // Mutation: let the landing count a held-back field's errors as disclosed, and the block shows
    // no explanation while the summary names Name.
    [Fact]
    public async Task A_spent_submit_that_keeps_the_held_back_set_explains_the_block_with_the_form_message()
    {
        using var form = Member.Build(_clock, wholeProfile: false, everyFieldShows: true);
        var modelLevel = new FieldIdentifier(form.Model, string.Empty);
        form.Notify(nameof(Member.Other)); // the stored answer says Name is required
        await form.SettleAsync(_time);
        var before = form.Rules.Gate.Runs;

        form.Rules.Gate.Hold = true;
        var submit = form.Engine.ValidateForSubmitAsync();
        Assert.True(await Until(() => form.Rules.Gate.Runs == before + 1));
        foreach (var other in new[] { "y", "z" })
        {
            var check = form.Rules.Gate.Runs;
            form.Model.Other = other;
            form.Notify(nameof(Member.Other));
            form.Rules.Gate.ReleaseHeldRuns(); // that check's answer predates the edit
            Assert.True(await Until(() => form.Rules.Gate.Runs == check + 1));
        }

        form.Engine.ApplyServerIssues([new ValidationIssue(nameof(Member.Name), "Name looks odd", ValidationSeverity.Warning)]);
        form.Model.Other = "w";
        form.Notify(nameof(Member.Other)); // outdates the last check
        form.Rules.Gate.ReleaseHeldRuns();
        var outcome = await submit;

        Assert.False(outcome.CanProceed);
        Assert.Equal(["Name looks odd"], form.Messages(nameof(Member.Name)));
        Assert.Contains(form.Engine.GetIssues(modelLevel), i => i.Message == GateText);
        Assert.DoesNotContain("Name", outcome.VisibleErrorSummary);

        form.Rules.Gate.Release();
        await form.SettleAsync(_time);
        Assert.Equal(["Name required", Member.NameTooShort], form.Messages(nameof(Member.Name)));
        Assert.DoesNotContain(form.Engine.GetIssues(modelLevel), i => i.Message == GateText);
    }

    // A whole-form check that began before an edit answers for the values before it, so its landing
    // brings back nothing a later reply held back. "recheck" is the form's opening check, still
    // waiting on the async rule when the visitor types and the reply arrives; a LiveDebounce window
    // keeps the edit from cancelling it. "load" is a load the visitor edited during. Either way the
    // reply starts a re-check of its own, and once that lands the client's answer for "ab" joins
    // the server's. Every read shows the server's answer alone until then, never "Code required".
    // Mutations: let any landing release what the reply held back, and the older load brings back
    // "Code required" (the older re-check never lands, because the reply's own re-check cancels
    // it); let a check begun before the edit count as the one that releases it, and the reply starts
    // no re-check, so the client's answer never joins.
    [Theory]
    [InlineData("recheck")]
    [InlineData("load")]
    public async Task A_check_begun_before_an_edit_brings_back_nothing_a_reply_held_back(string older)
    {
        var model = new Ticket();
        var rules = new AsyncTicketRules();
        var options = new FormidableOptions
        {
            LiveProfile = ValidationProfile.Draft,
            LiveDebounce = older == "recheck" ? TimeSpan.FromMilliseconds(200) : null,
        };
        var cut = RenderTicket(model, new FluentValidationModelValidator<Ticket>(rules), options);
        var engine = EngineOf(cut);
        var form = cut.FindComponent<FormidableForm<Ticket>>().Instance;
        var code = CodeOf(model);
        Task? load = null;

        if (older == "recheck")
        {
            rules.Gate.Hold = true;
            await cut.InvokeAsync(() => _time.Advance(PastRefresh)); // the opening check starts and waits
        }
        else
        {
            await SettleAsync(cut, engine);
            var opened = rules.Gate.Runs;
            rules.Gate.Hold = true;
            load = cut.InvokeAsync(() => form.DiscloseLoadedValuesAsync()); // starts and waits
            Assert.True(await Until(() => rules.Gate.Runs == opened + 1));
        }

        Assert.True(engine.IsValidating);
        var runs = rules.Gate.Runs;
        await EditAsync(cut, "Code", "ab");

        await ApplyAsync(cut, new ValidationIssue(nameof(Ticket.Code), CodeTaken));
        Assert.Equal([CodeTaken], Messages(engine, code));

        await cut.InvokeAsync(() => _clock.RunZeroWaits());
        Assert.Equal([CodeTaken], Messages(engine, code));

        rules.Gate.ReleaseHeldRuns(); // the older check lands, or is cancelled
        if (load is not null)
        {
            await load;
        }

        cut.WaitForState(() => !engine.IsValidating);
        await cut.InvokeAsync(() => { });
        Assert.DoesNotContain(CodeRequired, Messages(engine, code));

        rules.Gate.Release();
        await SettleAsync(cut, engine);
        await SettleAsync(cut, engine);
        Assert.Equal([CodeTooShort, CodeTaken], Messages(engine, code));
        Assert.Equal(1, rules.Gate.Runs - runs);

        await Services.DisposeAsync();
    }

    // ================================================================ the reply itself ====

    // The re-check a reply starts began after the reply, with nothing changed since, so it leaves the
    // reply standing: the client's answer for "ab" joins the server's message rather than replacing
    // it, and both stay.
    [Fact]
    public async Task The_recheck_a_reply_starts_leaves_the_reply_standing()
    {
        var model = new Ticket();
        var rules = new TicketRules();
        var cut = RenderTicket(model, new FluentValidationModelValidator<Ticket>(rules), TicketOptions("draft"));
        var engine = EngineOf(cut);
        var code = CodeOf(model);
        await SettleAsync(cut, engine);
        await EditAsync(cut, "Code", "ab");
        await SettleAsync(cut, engine);
        var runs = rules.Runs;

        await ApplyAsync(cut, new ValidationIssue(nameof(Ticket.Code), CodeTaken));
        Assert.Equal([CodeTaken], Messages(engine, code));

        await SettleAsync(cut, engine);
        Assert.Equal([CodeTooShort, CodeTaken], Messages(engine, code));
        await SettleAsync(cut, engine, TimeSpan.FromSeconds(5));
        Assert.Equal([CodeTooShort, CodeTaken], Messages(engine, code));
        Assert.Equal(runs + 1, rules.Runs);

        await Services.DisposeAsync();
    }

    // An edit or a field-set move between the reply and its re-check delays that re-check to the
    // usual wait, and its landing then replaces the reply, as any re-check after such a change does.
    // One re-check runs for the reply and the change together when the change is queued ahead of
    // the re-check's start. Until it lands, Code shows the server's answer alone.
    [Theory]
    [InlineData("edit")]
    [InlineData("move")]
    public async Task A_change_before_that_recheck_lets_it_replace_the_reply(string change)
    {
        var model = new Ticket();
        var rules = new TicketRules();
        var cut = RenderTicket(model, new FluentValidationModelValidator<Ticket>(rules), TicketOptions("draft"));
        var engine = EngineOf(cut);
        var code = CodeOf(model);
        await SettleAsync(cut, engine);
        await EditAsync(cut, "Code", "ab");
        await SettleAsync(cut, engine);
        var runs = rules.Runs;

        await ApplyAsync(cut, new ValidationIssue(nameof(Ticket.Code), CodeTaken));
        if (change == "edit")
        {
            await EditAsync(cut, "Code", "abcd");
        }
        else
        {
            await cut.InvokeAsync(() => cut.Render(p => p.Add(h => h.ShowNote, true)));
            await cut.InvokeAsync(() => { }); // the posted reconcile
        }

        Assert.Equal(0, _clock.ZeroWaits);
        Assert.Equal([CodeTaken], Messages(engine, code));

        await SettleAsync(cut, engine);
        Assert.Equal(change == "edit" ? [] : [CodeTooShort], Messages(engine, code));
        Assert.Equal(runs + 1, rules.Runs);

        await Services.DisposeAsync();
    }

    // A field whose answer a reply held back explains nothing on the form's behalf. A second reply
    // names only Note, which nothing renders, so Code, on screen, shows nothing: the server's message
    // went with the first reply, and its client answer is still held back. The form-level message
    // stands in until the re-check lands and Code shows its own answer, and the edit context, which
    // a native message component reads, says the same thing at each step.
    // Mutation: let the gate count a field whose client answer is held back, and the form-level
    // message is missing after the second reply.
    [Fact]
    public async Task A_field_whose_answer_is_held_back_leaves_the_block_to_the_form_message_until_its_answer_lands()
    {
        var model = new Ticket();
        var rules = new TicketRules();
        var cut = RenderTicket(model, new FluentValidationModelValidator<Ticket>(rules), TicketOptions("draft"));
        var engine = EngineOf(cut);
        var code = CodeOf(model);
        var modelLevel = new FieldIdentifier(model, string.Empty);
        await SettleAsync(cut, engine);
        await EditAsync(cut, "Code", "ab");
        await SettleAsync(cut, engine);

        await ApplyAsync(cut, new ValidationIssue(nameof(Ticket.Code), CodeTaken));
        Assert.DoesNotContain(GateText, Messages(engine, modelLevel));

        await ApplyAsync(cut, new ValidationIssue(nameof(Ticket.Note), "Note rejected"));
        Assert.Empty(Messages(engine, code));
        Assert.Equal([GateText], Messages(engine, modelLevel));
        Assert.Equal([GateText], engine.EditContext.GetValidationMessages(modelLevel));

        await cut.InvokeAsync(() => _clock.RunZeroWaits());
        Assert.Equal([CodeTooShort], Messages(engine, code));
        Assert.Empty(Messages(engine, modelLevel));
        Assert.Empty(engine.EditContext.GetValidationMessages(modelLevel));
        Assert.Equal([CodeTooShort], engine.EditContext.GetValidationMessages(code));

        await Services.DisposeAsync();
    }

    // ================================================================ several fixes on one form ====

    // A form with no summary, a hidden field, an async rule and a narrowed LiveProfile. The page sets
    // Code, which nothing renders, to "ab"; the visitor types a name, whose async rule is still
    // running when the server's reply about Code arrives, and edits the name again while a check
    // runs. Nothing on screen explains the refused save, so the form-level message shows; "checking"
    // stays on Name while its rule runs; and Code never shows "Code is required", which is about the
    // empty value it no longer holds.
    [Fact]
    public async Task Stacked_gate_pending_and_stale_answer_on_one_form()
    {
        var model = new Shipment();
        var rules = new ShipmentRules();
        var cut = Render(builder =>
        {
            builder.OpenComponent<FormidableForm<Shipment>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<Shipment>.Model), model);
            builder.AddComponentParameter(2, nameof(FormidableForm<Shipment>.Validator), (IModelValidator<Shipment>)new FluentValidationModelValidator<Shipment>(rules));
            builder.AddComponentParameter(3, nameof(FormidableForm<Shipment>.Options), new FormidableOptions { LiveProfile = ValidationProfile.Draft });
            builder.AddComponentParameter(4, nameof(FormidableForm<Shipment>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                AddInput(inner, 0, "Name", this, () => model.Name, v => model.Name = v ?? string.Empty);
                inner.OpenComponent<FormidableModelMessage>(1);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });
        var form = cut.FindComponent<FormidableForm<Shipment>>().Instance;
        var engine = form.Engine!;
        var name = FieldIdentifier.Create(() => model.Name);
        var code = FieldIdentifier.Create(() => model.Code);
        var modelLevel = new FieldIdentifier(model, string.Empty);

        void AssertNoStaleCode()
        {
            Assert.Equal([ShipmentRules.CodeTooShort], Messages(engine, code));
            Assert.DoesNotContain(engine.GetVisibleIssues(), v => v.Issue.Message == ShipmentRules.CodeRequired);
        }

        void AssertGateOnScreen()
        {
            var list = Assert.Single(cut.FindAll("ul"), ul => ul.Id == FormidableFieldId.MessagesFor(modelLevel));
            Assert.Equal([GateText], list.Children.Select(li => li.TextContent.Trim()).ToList());
        }

        await SettleAsync(cut, engine);
        await cut.InvokeAsync(() =>
        {
            model.Code = "ab"; // the page sets the hidden field
            engine.EditContext.NotifyFieldChanged(code);
        });
        await SettleAsync(cut, engine);
        var runs = rules.Gate.Runs;

        rules.Gate.Hold = true;
        await EditAsync(cut, "Name", "Ada");
        Assert.True(engine.GetFieldState(name).IsValidating);

        await cut.InvokeAsync(() => form.ApplyServerIssues([new ValidationIssue(nameof(Shipment.Code), ShipmentRules.CodeTooShort)]));
        AssertGateOnScreen();
        AssertNoStaleCode();
        Assert.True(engine.GetFieldState(name).IsValidating);

        await EditAsync(cut, "Name", "Adam"); // while Name's check runs
        Assert.True(engine.GetFieldState(name).IsValidating);
        AssertGateOnScreen();
        AssertNoStaleCode();

        await cut.InvokeAsync(() => _clock.RunZeroWaits());
        AssertNoStaleCode();

        rules.Gate.Release();
        await SettleAsync(cut, engine);
        await SettleAsync(cut, engine);
        Assert.False(engine.GetFieldState(name).IsValidating);
        AssertGateOnScreen();
        AssertNoStaleCode();
        Assert.Empty(Messages(engine, name));
        Assert.Equal(2, rules.Gate.Runs - runs);

        await Services.DisposeAsync();
    }

    // ================================================================ helpers ====

    private static FormidableOptions TicketOptions(string config) => config switch
    {
        "draft" or "async-held-draft" => new FormidableOptions { LiveProfile = ValidationProfile.Draft },
        "draft+tracking" => new FormidableOptions { LiveProfile = ValidationProfile.Draft, TrackFormValidity = true },
        "draft+recheck-off" => new FormidableOptions { LiveProfile = ValidationProfile.Draft, RefreshDebounce = Timeout.InfiniteTimeSpan },
        "default" or "async-held-default" => new FormidableOptions(),
        "default+tracking" => new FormidableOptions { TrackFormValidity = true },
        "submit-same-instance" => new FormidableOptions { LiveProfile = ValidationProfile.Submit },
        "submit-equal-copy" => new FormidableOptions { LiveProfile = ValidationProfile.Named("Submit", includeDefaultRules: true, "Submit") },
        "wider-than-submit" => new FormidableOptions { LiveProfile = ValidationProfile.Named("Live", includeDefaultRules: true, "Submit", "Live") },
        "window-never-closes" => new FormidableOptions { LiveDebounce = Timeout.InfiniteTimeSpan },
        "window-400ms" => new FormidableOptions { LiveDebounce = TimeSpan.FromMilliseconds(400) },
        _ => throw new ArgumentOutOfRangeException(nameof(config), config, null),
    };

    private IRenderedComponent<TicketHost> RenderTicket(Ticket model, IModelValidator<Ticket> validator, FormidableOptions options) =>
        Render<TicketHost>(p => p
            .Add(h => h.Model, model)
            .Add(h => h.Validator, validator)
            .Add(h => h.Options, options));

    private IRenderedComponent<AccountHost> RenderAccount(Account model, AccountRules rules, FormidableOptions options) =>
        Render<AccountHost>(p => p
            .Add(h => h.Model, model)
            .Add(h => h.Validator, new FluentValidationModelValidator<Account>(rules))
            .Add(h => h.Options, options));

    private static IFormidableEngine EngineOf(IRenderedComponent<IComponent> cut) =>
        cut.FindComponents<FormidableForm<Ticket>>() is [var ticket]
            ? ticket.Instance.Engine!
            : cut.FindComponent<FormidableForm<Account>>().Instance.Engine!;

    private static FieldIdentifier CodeOf(Ticket model) => FieldIdentifier.Create(() => model.Code);

    private static List<string> Messages(IFormidableEngine engine, FieldIdentifier field) =>
        [.. engine.GetIssues(field).Select(i => i.Message)];

    private static Task EditAsync(IRenderedComponent<IComponent> cut, string entry, string value) =>
        cut.InvokeAsync(() => cut.Find($"[data-entry=\"{entry}\"] input").Change(value));

    private static async Task ApplyAsync(IRenderedComponent<IComponent> cut, params ValidationIssue[] issues)
    {
        var ticket = cut.FindComponents<FormidableForm<Ticket>>();
        if (ticket is [var form])
        {
            await cut.InvokeAsync(() => form.Instance.ApplyServerIssues(issues));
            return;
        }

        await cut.InvokeAsync(() => cut.FindComponent<FormidableForm<Account>>().Instance.ApplyServerIssues(issues));
    }

    // Runs any zero wait a step left, moves the clock on, waits for every check to land, then lets a
    // posted reconcile or republish run.
    private async Task SettleAsync(IRenderedComponent<IComponent> cut, IFormidableEngine engine, TimeSpan? by = null)
    {
        await cut.InvokeAsync(() => _clock.RunZeroWaits());
        await cut.InvokeAsync(() => _time.Advance(by ?? PastRefresh));
        cut.WaitForState(() => !engine.IsValidating);
        await cut.InvokeAsync(() => { });
        await cut.InvokeAsync(() => _clock.RunZeroWaits());
        cut.WaitForState(() => !engine.IsValidating);
        await cut.InvokeAsync(() => { });
    }

    private static async Task<bool> Until(Func<bool> condition)
    {
        for (var waited = 0; waited < 500 && !condition(); waited++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    private static void AddInput(RenderTreeBuilder builder, int region, string entry, object receiver, Expression<Func<string?>> accessor, Action<string?> assign, object? key = null)
    {
        builder.OpenRegion(region);
        builder.OpenElement(0, "div");
        if (key is not null)
        {
            builder.SetKey(key);
        }

        builder.AddAttribute(1, "data-entry", entry);
        builder.OpenComponent<FormidableInputText>(2);
        builder.AddComponentParameter(3, nameof(FormidableInputText.Value), accessor.Compile()());
        builder.AddComponentParameter(4, nameof(FormidableInputText.ValueChanged), EventCallback.Factory.Create(receiver, assign));
        builder.AddComponentParameter(5, nameof(FormidableInputText.ValueExpression), accessor);
        builder.CloseComponent();
        builder.OpenComponent<FormidableFieldMessage<string?>>(6);
        builder.AddComponentParameter(7, nameof(FormidableFieldMessage<string?>.For), accessor);
        builder.CloseComponent();
        builder.CloseElement();
        builder.CloseRegion();
    }

    /// <summary>
    /// A clock over a <see cref="FakeTimeProvider"/> that holds every timer armed at zero until
    /// <see cref="RunZeroWaits"/> runs it, so the callback runs after the arming code has returned.
    /// A later arm of the same timer replaces a held one, which models a change already queued
    /// ahead of the fire: a real timer armed at zero posts its fire within a couple of
    /// milliseconds, and a later re-arm cannot recall that post. Any other wait runs on the fake
    /// clock as usual.
    /// </summary>
    private sealed class ZeroWaitClock(FakeTimeProvider clock) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<HeldTimer> _held = [];

        /// <summary>How many timers armed at zero are waiting to run.</summary>
        public int ZeroWaits
        {
            get
            {
                lock (_sync)
                {
                    return _held.Count;
                }
            }
        }

        public override long GetTimestamp() => clock.GetTimestamp();

        public override long TimestampFrequency => clock.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

        public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new HeldTimer(this, callback, state, clock.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.Change(dueTime, period);
            }

            return timer;
        }

        /// <summary>Runs every timer held at zero, in the order they were armed, and returns how many ran.</summary>
        public int RunZeroWaits()
        {
            List<HeldTimer> due;
            lock (_sync)
            {
                due = [.. _held];
                _held.Clear();
            }

            foreach (var timer in due)
            {
                timer.Run();
            }

            return due.Count;
        }

        private void Hold(HeldTimer timer, bool held)
        {
            lock (_sync)
            {
                _held.Remove(timer);
                if (held)
                {
                    _held.Add(timer);
                }
            }
        }

        private sealed class HeldTimer(ZeroWaitClock owner, TimerCallback callback, object? state, ITimer timer) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                var zero = dueTime == TimeSpan.Zero;
                owner.Hold(this, zero);
                return timer.Change(zero ? Timeout.InfiniteTimeSpan : dueTime, period);
            }

            public void Run() => callback(state);

            public void Dispose()
            {
                owner.Hold(this, false);
                timer.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                owner.Hold(this, false);
                return timer.DisposeAsync();
            }
        }
    }

    // ================================================================ models and rules ====

    /// <summary>Counts each run of an async rule as it starts and, while <see cref="Hold"/> is set, makes the run wait until released.</summary>
    public sealed class RuleGate
    {
        private volatile bool _hold;
        private int _runs;
        private volatile TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How many runs have started.</summary>
        public int Runs => Volatile.Read(ref _runs);

        /// <summary>Whether a run that starts waits.</summary>
        public bool Hold
        {
            get => _hold;
            set => _hold = value;
        }

        /// <summary>Lets every waiting run, and every later one, answer.</summary>
        public void Release()
        {
            Hold = false;
            _gate.TrySetResult();
        }

        /// <summary>Makes every waiting run throw, and lets every later run answer.</summary>
        public void Fault()
        {
            Hold = false;
            _gate.TrySetException(new InvalidOperationException("the rule faulted"));
        }

        /// <summary>Lets the waiting runs answer, and makes every later run wait on a fresh gate.</summary>
        public void ReleaseHeldRuns()
        {
            var held = _gate;
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            held.TrySetResult();
        }

        /// <summary>Counts a run and waits while <see cref="Hold"/> is set.</summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            if (Hold)
            {
                await _gate.Task.WaitAsync(cancellationToken);
            }
        }
    }

    /// <summary>A code, and a note no rule reads.</summary>
    public sealed class Ticket
    {
        public string Code { get; set; } = string.Empty;

        public string Note { get; set; } = string.Empty;
    }

    /// <summary>Code's two rules in the Submit rule set beside a rule that counts the set's runs, and a Live set whose one rule passes. No rule sits outside a set, so Draft selects nothing.</summary>
    private sealed class TicketRules : AbstractValidator<Ticket>
    {
        private int _runs;

        public TicketRules()
        {
            RuleSet("Submit", () =>
            {
                RuleFor(t => t.Code).NotEmpty().WithMessage(CodeRequired).MinimumLength(3).WithMessage(CodeTooShort);
                RuleFor(t => t.Code).Must(_ =>
                {
                    Interlocked.Increment(ref _runs);
                    return true;
                });
            });
            RuleSet("Live", () => RuleFor(t => t.Code).Must(_ => true));
        }

        /// <summary>How many times the Submit rule set has run.</summary>
        public int Runs => Volatile.Read(ref _runs);
    }

    /// <summary>Code is required, and an async rule in the Submit rule set says a code under three characters is too short.</summary>
    private sealed class AsyncTicketRules : AbstractValidator<Ticket>
    {
        public AsyncTicketRules()
        {
            RuleSet("Submit", () =>
            {
                RuleFor(t => t.Code).NotEmpty().WithMessage(CodeRequired);
                RuleFor(t => t.Code).MustAsync(async (value, ct) =>
                {
                    await Gate.RunAsync(ct);
                    return value.Length >= 3;
                }).WithMessage(CodeTooShort);
            });
        }

        public RuleGate Gate { get; } = new();
    }

    /// <summary>A form over <see cref="Ticket"/>: a kit input for Code, and one for Note while <see cref="ShowNote"/> holds.</summary>
    private sealed class TicketHost : ComponentBase
    {
        [Parameter]
        public Ticket Model { get; set; } = default!;

        [Parameter]
        public IModelValidator<Ticket> Validator { get; set; } = default!;

        [Parameter]
        public FormidableOptions Options { get; set; } = default!;

        [Parameter]
        public bool ShowNote { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<Ticket>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<Ticket>.Model), Model);
            builder.AddComponentParameter(2, nameof(FormidableForm<Ticket>.Validator), Validator);
            builder.AddComponentParameter(3, nameof(FormidableForm<Ticket>.Options), Options);
            builder.AddComponentParameter(4, nameof(FormidableForm<Ticket>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                AddInput(inner, 0, "Code", this, () => Model.Code, v => Model.Code = v ?? string.Empty);
                if (ShowNote)
                {
                    AddInput(inner, 1, "Note", this, () => Model.Note, v => Model.Note = v ?? string.Empty);
                }
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>A name and a code, both empty.</summary>
    public sealed class Account
    {
        public string Name { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;
    }

    /// <summary>Both fields required, and an async rule that says a code under three characters is too short, all in the submit bucket.</summary>
    private sealed class AccountRules : DraftSubmitValidator<Account>
    {
        public RuleGate Gate { get; } = new();

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(a => a.Name).NotEmpty().WithMessage("Name required");
            RuleFor(a => a.Code).NotEmpty().WithMessage(CodeRequired);
            RuleFor(a => a.Code).MustAsync(async (value, ct) =>
            {
                await Gate.RunAsync(ct);
                return value.Length >= 3;
            }).WithMessage(CodeTooShort);
        }
    }

    /// <summary>A form over <see cref="Account"/> with a kit input for each field.</summary>
    private sealed class AccountHost : ComponentBase
    {
        [Parameter]
        public Account Model { get; set; } = default!;

        [Parameter]
        public IModelValidator<Account> Validator { get; set; } = default!;

        [Parameter]
        public FormidableOptions Options { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<Account>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<Account>.Model), Model);
            builder.AddComponentParameter(2, nameof(FormidableForm<Account>.Validator), Validator);
            builder.AddComponentParameter(3, nameof(FormidableForm<Account>.Options), Options);
            builder.AddComponentParameter(4, nameof(FormidableForm<Account>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                AddInput(inner, 0, "Name", this, () => Model.Name, v => Model.Name = v ?? string.Empty);
                AddInput(inner, 1, "Code", this, () => Model.Code, v => Model.Code = v ?? string.Empty);
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>A name the visitor types, and a code the page sets and nothing renders.</summary>
    public sealed class Shipment
    {
        public string Name { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;
    }

    /// <summary>Name's async rule in the draft bucket, so the narrowed live channel runs it; both fields' presence and Code's length in the submit bucket.</summary>
    private sealed class ShipmentRules : DraftSubmitValidator<Shipment>
    {
        public const string CodeRequired = "Code is required";
        public const string CodeTooShort = "Code is too short";

        public RuleGate Gate { get; } = new();

        protected override void ConfigureDraftRules() =>
            RuleFor(s => s.Name).MustAsync(async (value, ct) =>
            {
                await Gate.RunAsync(ct);
                return value != "Taken";
            }).WithMessage("Name is taken");

        protected override void ConfigureSubmitRules()
        {
            RuleFor(s => s.Name).NotEmpty().WithMessage("Name is required");
            RuleFor(s => s.Code).NotEmpty().WithMessage(CodeRequired).MinimumLength(3).WithMessage(CodeTooShort);
        }
    }

    /// <summary>The /server page's order, line list and all.</summary>
    public sealed class Order
    {
        public string Description { get; set; } = string.Empty;

        public List<OrderLine> Lines { get; set; } = [new OrderLine()];
    }

    public sealed class OrderLine
    {
        public string Sku { get; set; } = string.Empty;
    }

    /// <summary>The /server page's validator, rule for rule.</summary>
    private sealed class OrderValidator : DraftSubmitValidator<Order>
    {
        public const string HyphenWarning = "Hyphens make order references harder to read aloud";

        protected override void ConfigureDraftRules()
        {
        }

        protected override void ConfigureSubmitRules()
        {
            RuleFor(o => o.Description).NotEmpty().WithMessage("Description is required");
            RuleFor(o => o.Description)
                .Must(d => !d.Contains('-'))
                .WithSeverity(Severity.Warning)
                .WithMessage(HyphenWarning);
            RuleForEach(o => o.Lines).ChildRules(line =>
                line.RuleFor(l => l.Sku).NotEmpty().WithMessage("SKU is required"));
        }
    }

    /// <summary>The /server page's markup in miniature: Description, one keyed row per line, and Add line.</summary>
    public sealed class OrderHost : ComponentBase
    {
        [Parameter]
        public FormidableOptions Options { get; set; } = new();

        public Order Model { get; } = new();

        public FormidableForm<Order>? Form { get; private set; }

        public void AddLine()
        {
            Model.Lines.Add(new OrderLine());
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<FormidableForm<Order>>(0);
            builder.AddComponentParameter(1, nameof(FormidableForm<Order>.Model), Model);
            builder.AddComponentParameter(2, nameof(FormidableForm<Order>.Validator), (IModelValidator<Order>)new FluentValidationModelValidator<Order>(new OrderValidator()));
            builder.AddComponentParameter(3, nameof(FormidableForm<Order>.Options), Options);
            builder.AddComponentParameter(4, nameof(FormidableForm<Order>.ChildContent), (RenderFragment<FormidableFormContext>)(_ => inner =>
            {
                AddInput(inner, 0, "Description", this, () => Model.Description, v => Model.Description = v ?? string.Empty);
                for (var i = 0; i < Model.Lines.Count; i++)
                {
                    var line = Model.Lines[i];
                    AddInput(inner, 1, $"Sku{i}", this, () => line.Sku, v => line.Sku = v ?? string.Empty, key: line);
                }
            }));
            builder.AddComponentReferenceCapture(5, r => Form = (FormidableForm<Order>)r);
            builder.CloseComponent();
        }
    }

    /// <summary>A name the visitor types, an other field whose async rule sits in the submit bucket, and a third field a submit can block on.</summary>
    public sealed class Member
    {
        public const string NameTaken = "Name taken";
        public const string NameTooShort = "Name too short";

        public string Name { get; set; } = string.Empty;

        public string Other { get; set; } = "x";

        public string Third { get; set; } = "t";

        /// <summary>Builds an engine over a fresh member on the given clock, dispatching one delegate at a time as the renderer does.</summary>
        /// <param name="clock">The clock.</param>
        /// <param name="wholeProfile">Whether to hide the rule-by-rule seam, so every check is one whole-profile call.</param>
        /// <param name="everyFieldShows">Whether a disclosure override shows every issue; otherwise nothing renders any field.</param>
        internal static MemberForm Build(TimeProvider clock, bool wholeProfile, bool everyFieldShows = false)
        {
            var model = new Member();
            var editContext = new EditContext(model);
            var rules = new MemberRules();
            IModelValidator<Member> validator = new FluentValidationModelValidator<Member>(rules);
            if (wholeProfile)
            {
                validator = new WholeProfile(validator);
            }

            var options = new FormidableOptions { DisclosureOverride = everyFieldShows ? _ => true : null };
            var engine = new FormidableEngine<Member>(
                model, editContext, validator, new ReflectionModelIntrospector(), options, clock,
                EngineTestSync.OneAtATime());
            return new MemberForm(engine, editContext, model, rules);
        }
    }

    /// <summary>Name must be filled and at least three characters, and Third filled, in the draft bucket; Other's async rule, which always passes, in the submit bucket.</summary>
    internal sealed class MemberRules : DraftSubmitValidator<Member>
    {
        public RuleGate Gate { get; } = new();

        protected override void ConfigureDraftRules()
        {
            RuleFor(m => m.Name).NotEmpty().WithMessage("Name required").MinimumLength(3).WithMessage(Member.NameTooShort);
            RuleFor(m => m.Third).NotEmpty().WithMessage("Third required");
        }

        protected override void ConfigureSubmitRules() =>
            RuleFor(m => m.Other).MustAsync(async (_, ct) =>
            {
                await Gate.RunAsync(ct);
                return true;
            });
    }

    /// <summary>Hides the rule-by-rule seam, so every check is one whole-profile call that can reuse no stored answer.</summary>
    private sealed class WholeProfile(IModelValidator<Member> inner) : IModelValidator<Member>
    {
        public Task<ValidationReport> ValidateAsync(Member model, ValidationProfile profile, CancellationToken cancellationToken = default) =>
            inner.ValidateAsync(model, profile, cancellationToken);

        public ValidationReport Validate(Member model, ValidationProfile profile) => inner.Validate(model, profile);
    }

    internal sealed record MemberForm(FormidableEngine<Member> Engine, EditContext EditContext, Member Model, MemberRules Rules) : IDisposable
    {
        public void Notify(string field) => EditContext.NotifyFieldChanged(new FieldIdentifier(Model, field));

        public List<string> Messages(string field) => [.. Engine.GetIssues(new FieldIdentifier(Model, field)).Select(i => i.Message)];

        /// <summary>Moves the clock past the re-check's wait in steps and waits for every check to land.</summary>
        public async Task SettleAsync(FakeTimeProvider time)
        {
            for (var step = 0; step < 5; step++)
            {
                time.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(5);
            }

            Assert.True(await Until(() => !Engine.IsValidating));
        }

        public void Dispose() => Engine.Dispose();
    }
}
