# Async validation rules

**You should already know:** the two profiles and the live-versus-submit split
([Core concepts](core-concepts.md)), and what an async rule looks like from the
outside — `MustAsync`, plus the `IsValidating` pending flag a field carries while one is in
flight ([Async and server](async-and-server.md)).

Async validation is hard. Not hard to write, since `MustAsync` is one method, but hard to
*order*: the moment a rule takes real time to answer, its answer stops being about the value on
screen. A uniqueness check finishes half a second after the user has moved on to the next
field. An edit lands while the submit it interrupted is still running. Two keystrokes race, and
the older one's answer arrives last. A re-check nobody asked for wakes up mid-word. Every one
of those puts a verdict on screen for a value that no longer exists, and every one of them is
an ordering problem rather than a rule problem.

Formidable's answer is that exactly one validation pass is ever in flight, and the engine
decides which one it is: not the rule, and not the page. This page covers when an async rule
runs, what happens to a still-running check whose value has already gone stale, and how a UI
reports "this is still checking" without keeping any bookkeeping of its own.

## Need to know

Three things are yours: put the rule in the draft profile, honour its cancellation token, and
render the field's pending flag. The sequencing is the engine's.

Nothing about writing the rule changes. `MustAsync` and its siblings work in Formidable exactly
as they do in plain FluentValidation, awaited like any other rule. What decides *when* it runs
is the profile it sits in. A live pass — one per field change — validates whichever profile
`FormidableOptions.LiveProfile` names (`ValidationProfile.Draft` by default; see
[Profiles](profiles.md)). An async rule placed in `ConfigureDraftRules()` therefore
runs on every keystroke, not just at submit: the shape a live "is this username taken?" check
needs. It sits there for the same reason ordinary format rules do, since draft rules are the
ones that run while the user is still typing.

```csharp
using FluentValidation;

namespace Formidable.Sample.Shared;

public class Handle
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public class HandleValidator : DraftSubmitValidator<Handle>
{
    private static readonly string[] Taken = ["admin", "root", "formidable"];
    private static readonly string[] TakenDisplayNames = ["Administrator", "Root User", "Formidable"];

    // Mutable so the sample page can slow the simulated call down and make cancellation
    // visible; a real validator would inject a clock/service rather than hold mutable state.
    public static int SimulatedDelayMs { get; set; } = 600;

    protected override void ConfigureDraftRules()
    {
        // Async uniqueness runs in the live (Draft) profile so it fires as the user types;
        // the delay stands in for a server call and honours cancellation, so a superseded
        // keystroke's check is abandoned.
        RuleFor(h => h.Username)
            .MustAsync(async (username, cancellationToken) =>
            {
                await Task.Delay(SimulatedDelayMs, cancellationToken);
                return !Taken.Contains(username, StringComparer.OrdinalIgnoreCase);
            })
            .WithMessage("That username is taken")
            .When(h => !string.IsNullOrEmpty(h.Username));

        // A second, independent async field — demonstrates that the pending indicator during a
        // live pass is scoped to the field being edited, not the whole form.
        RuleFor(h => h.DisplayName)
            .MustAsync(async (displayName, cancellationToken) =>
            {
                await Task.Delay(SimulatedDelayMs, cancellationToken);
                return !TakenDisplayNames.Contains(displayName, StringComparer.OrdinalIgnoreCase);
            })
            .WithMessage("That display name is taken")
            .When(h => !string.IsNullOrEmpty(h.DisplayName));
    }

    protected override void ConfigureSubmitRules()
    {
        RuleFor(h => h.Username).NotEmpty().WithMessage("Username is required");
    }
}
```

*Source: `samples/Formidable.Sample.Shared/Handle.cs`*

The `CancellationToken` that `MustAsync` hands the rule is load-bearing, not decoration. A fast
run of keystrokes cancels each prior pass the moment the next one starts, and a rule that
ignores its token lets an abandoned pass keep running toward a result nobody will read. Worse,
toward a result that can still land after a *later* pass has already resolved and updated the
model's state.

`Username` and `DisplayName` are two unrelated async checks on the same model, which is worth
keeping in mind for the pending UI: the two never light each other up.

Then render the pending flag. `FormidableField`'s cascaded `FormidableFieldContext` exposes it
as `field.State.IsValidating`:

```razor
    <FormidableField For="() => _handle.Username" Context="field">
        <div class="field">
            <label>Username <FormidableInputText @bind-Value="_handle.Username" UpdateOn="InputUpdateMode.OnInput" /></label>
            @if (field.State.IsValidating)
            {
                <em role="status">checking…</em>
            }
        </div>
        <FormidableFieldMessage For="() => _handle.Username" />
    </FormidableField>

    <FormidableField For="() => _handle.DisplayName" Context="field">
        <div class="field">
            <label>Display name <FormidableInputText @bind-Value="_handle.DisplayName" UpdateOn="InputUpdateMode.OnInput" /></label>
            @if (field.State.IsValidating)
            {
                <em role="status">checking…</em>
            }
        </div>
        <FormidableFieldMessage For="() => _handle.DisplayName" />
    </FormidableField>
```

*Excerpt from `samples/Formidable.Sample/Pages/AsyncRules.razor`*

Both fields use `UpdateOn="InputUpdateMode.OnInput"` so a live pass starts on every keystroke,
not just on blur — otherwise there'd be nothing to cancel until the user tabbed away.

That is the whole authoring surface. The rest of this page is what the engine does with it.

## The ordering, end to end

Only one validation pass — live, submit, or the post-submit refresh — is ever in flight on the
engine at a time, and starting a new one cancels whatever pass came before it. Everything below
follows from that one constraint. The flowchart traces it end to end: what an edit starts, how
the refresh defers to whatever is already running, and how a submit sits above all of it.

```mermaid
flowchart TD
    A["Field edit commits"] --> B{"Is a submit in flight?"}
    B -- "yes" --> C["Live pass does not start for this edit"]
    B -- "no" --> D["Live pass starts for the changed field"]
    D --> E{"A newer live pass starts before this one finishes?"}
    E -- "yes" --> F["This pass is cancelled, superseded"]
    E -- "no" --> G["This pass wins: writes verdicts for its own field plus every field of the passes it superseded"]

    A --> H{"HasSubmitted, or a submit already in flight?"}
    H -- "no" --> I["No refresh armed"]
    H -- "yes" --> J["Field added to the pending-refresh set; debounced refresh timer (re)armed"]

    J --> K["Debounce quiets, 300 ms by default"]
    K --> L{"Submit or a live pass in flight?"}
    L -- "yes" --> M["Refresh defers: re-arms its timer instead of running"]
    M --> K
    L -- "no" --> N["Refresh pass runs SubmitProfile over the whole model; the pending indicator is scoped to the pending-refresh snapshot"]

    O["Submit invoked"] --> P["Submit pass runs SubmitProfile form-wide, cancelling whatever pass was in flight"]
    P --> Q["HasSubmitted set true"]
```

The diagram traces the default cadence, where an edit's live pass starts on the keystroke itself.
`FormidableOptions.LiveDebounce` puts a timer in front of that first step, described under
[The live pass starts](#the-live-pass-starts); everything downstream of it is unchanged.

### An edit commits

A keystroke is a small event with a large blast radius. The engine decides, at the moment the
field change lands, what work it starts — and the answer differs depending on whether the form
has been submitted yet.

Before any submit, an edit starts one thing: a live pass. Once the form has been submitted, the
same edit also arms a debounced refresh. Whether that live pass actually starts is the first
branch below; the refresh arming is the second.

### Is a submit in flight?

People keep typing while a spinner turns. If the keystroke landing mid-submit started its own
live pass, that pass would cancel the submit the user actually asked for, and the one operation
they explicitly requested would lose to one they didn't.

So it never starts. The live pass for an edit made while a submit is in flight does not begin:
submit is the higher-intent operation, and a live or refresh pass never supersedes it. The edit
is not dropped, though. It still lands in the pending-refresh set and arms the debounced
refresh, which defers to the submit for as long as it stays in flight and runs once the submit
lands.

### The live pass starts

Feedback that waits for blur is feedback the user has already outrun: they have typed six more
characters and moved on before anything told them the first three were a problem.

With no submit in flight, the edit starts its own pass immediately, for the field that changed.
There is no timer sitting in front of a live pass — it begins on the keystroke itself.

Unless you ask for one. `FormidableOptions.LiveDebounce` is `null` by default, which is the cadence
just described; set it and a field change arms a single shared timer instead of starting a pass.
Another edit inside the window re-arms that timer rather than opening a second one, and when the
window elapses quietly, one live pass runs, scoped to every field the window collected. It is worth
reaching for when the live rules are expensive enough that one pass per keystroke is the wrong
trade — an async availability check being the obvious case, since supersession still costs a
started-and-cancelled request per keystroke.

A debounced live pass is a little more deferential than an immediate one: with a submit *or* a
refresh already running, it re-arms its timer instead of starting, because the edit that would
normally re-arm the refresh has already happened, and cancelling the refresh outright would leave
its verdict stale with nothing left to fix it. What the option does not touch is the refresh's own
cadence. After a submit, every keystroke still lands in the pending-refresh set and still arms the
refresh on `RefreshDebounce`'s schedule, whatever `LiveDebounce` says — the two windows are
independent, and the option reduces live passes, not refresh passes.

### A newer pass supersedes an older one

Type into a field whose uniqueness check takes half a second and, left ungoverned, several
answers are in the air at once for one field. Nothing about async guarantees they come back in
the order they were asked, so the earliest question can produce the last answer, and the last
answer is the one the user is left looking at.

That can't happen here, because starting a pass cancels the one before it, which is exactly why
an async rule has to honour its token. The cancelled pass writes nothing: it is no longer the
authority on anything. The pass that wins writes the verdicts for the fields of the passes it
superseded as well as its own, so a field edited moments before another still gets its answer.

### After a submit, an edit also arms a refresh

A failed submit leaves errors on screen and the user starts fixing them. Those messages came
from the submit pass, so keeping them honest means re-running the submit profile. Doing that on
every keystroke, for a whole form, is a lot of work to spend on a user who is still mid-word.

That is what the refresh is for: keeping already-visible submit errors and warnings current
without re-validating the whole profile on every keystroke of a form the user is still
correcting. Once `HasSubmitted` is true, or while a submit is in flight, every further field
change adds that field to the pending-refresh set and reschedules a debounced re-run of
`SubmitProfile`. Before the first submit there is nothing to arm: an edit starts a live pass and
nothing else.

### The debounce quiets

A refresh should follow the user's pauses, not their keystrokes.

The timer fires after `FormidableOptions.RefreshDebounce` of quiet (300 ms by default; see
[Options](options.md)). This is the one timer-based debounce a form gets without asking. A live
pass has none unless [`LiveDebounce`](options.md#livedebounce) puts one there: by default it starts
on the keystroke and resolves a burst by supersession instead of by waiting.

### The refresh defers to whatever is running

The timer firing means the user stopped typing. It does not mean the engine is free. A submit
may be in flight. A live pass may be mid-rule, holding the answer the user is actually waiting
for. Since starting a pass cancels the one before it, a refresh that ran regardless would throw
away work someone asked for. The async rule that was 500 ms into a 600 ms check would have
nothing to show for it.

So it defers, to either one. If a submit or a live pass is in flight, the refresh re-arms its
own timer rather than running, and comes back when the timer next quiets — as many times as it
takes. Deferring to a submit is the higher-intent rule again. Deferring to a live pass earns its
keep somewhere else: an edit whose async rule outlasts the debounce still gets the answer its
own live pass was computing. Between live passes there is nothing to defer to, since a newer
live pass supersedes the older one outright. The refresh's own path is different: it cancels
neither the submit nor the live pass, it waits for them.

With nothing in flight, the refresh pass runs `SubmitProfile`. It re-validates the whole model
in one pass; what the pending-refresh snapshot scopes is the pending indicator, covered in
[Which fields show "checking…"](#which-fields-show-checking) below.

### Submit sits above all of it

Submit is the moment the user commits the form. Whatever partial work was in flight for a
half-typed field is beside the point now, and the answer they are owed is the one about the
whole model.

Submit enters the flowchart on its own edge, because nothing an edit does starts it. The submit
pass runs `SubmitProfile` form-wide, cancelling whatever pass was in flight, and it is never
superseded in turn. It sets `HasSubmitted` true, which is what arms the refresh for every edit
that follows.

### One edit after a submit runs the draft rules twice

Once a form has been submitted, an edit takes both branches of the flowchart at once: it starts
a live pass and it arms the refresh. `ValidationProfile.Submit` is the default rules *plus* the
`Submit` ruleset, so an async rule written in `ConfigureDraftRules()` — the uniqueness check at
the top of this page — runs in both. The sample's
[`/async`](../samples/Formidable.Sample/Pages/AsyncRules.razor) page makes it visible. Set the
delay slider to 2000 ms, submit, then type: the check resolves, and a second one starts.

The engine does not collapse that into one run, because the two passes answer different
questions and own different channels. The live pass answers "is the value on screen acceptable
right now?", and its verdict is what surfaces a problem on a field that was clean at submit
time. The refresh answers "are the messages the submit put on screen still true?", and its
verdict reaches only the fields already disclosed at submit time. Neither report can be
rewritten into the other. The live report is missing every `Submit`-ruleset rule; the refresh's
report has both halves mixed together and no record of which ruleset produced which issue, so
it cannot be split back apart. Substituting one for the other is how a submit-time message goes
stale: it stays on screen after the value it accuses has been fixed.

Two things reduce the cost, and both of them are yours rather than the engine's:

- [`LiveDebounce`](options.md#livedebounce) collapses a burst of keystrokes into a single live
  pass. It leaves the refresh's own cadence alone, as described under
  [The live pass starts](#the-live-pass-starts): it reduces live passes, not refresh passes, so
  it never takes a post-submit edit below the two runs described here.
- Memoize inside the rule when the check is genuinely expensive, keyed on the value the rule is
  checking. The rule has that value in scope and the engine deliberately does not: it hands the
  validator a profile and takes back a report, with no rule-level seam to cache at. That makes
  the remedy the validator's, and it is a small one.

The key is the value itself: a second pass over an unchanged value reuses the first pass's answer
instead of making the call again. The two runs are about one `RefreshDebounce` apart — 300 ms by
default — so the window only has to be long enough to catch a duplicate that is milliseconds
old.

Here is the same uniqueness check as a validator that calls a real directory service
(`IUsernameDirectory` below is the consumer's own lookup, whatever it is) and remembers its last
answer. It is a second validator over the same `Handle` model, not the sample's own
`HandleValidator` quoted above:

```csharp
using System.Diagnostics;
using FluentValidation;
using Formidable;

public class UniqueHandleValidator : DraftSubmitValidator<Handle>
{
    private static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(1);
    private readonly IUsernameDirectory _directory;
    private (string Username, bool Free, long Timestamp)? _last;

    public UniqueHandleValidator(IUsernameDirectory directory) => _directory = directory;

    protected override void ConfigureDraftRules() =>
        RuleFor(h => h.Username)
            .MustAsync((username, token) => IsFreeAsync(username, token))
            .WithMessage("That username is taken")
            .When(h => !string.IsNullOrEmpty(h.Username));

    protected override void ConfigureSubmitRules() =>
        RuleFor(h => h.Username)
            .NotEmpty()
            .WithMessage("A username is required");

    private async Task<bool> IsFreeAsync(string username, CancellationToken cancellationToken)
    {
        if (_last is { } last
            && last.Username == username
            && Stopwatch.GetElapsedTime(last.Timestamp) < CacheWindow)
        {
            return last.Free;
        }

        var free = await _directory.IsFreeAsync(username, cancellationToken);
        _last = (username, free, Stopwatch.GetTimestamp());
        return free;
    }
}
```

Three things decide whether that is safe on a given rule:

- **The check has to be pure.** Its answer may depend on the value and nothing else. A rule that
  reads another field, the clock, or a row a colleague is editing at the same time has no
  business remembering its last answer.
- **The validator's lifetime is the cache's lifetime.** A scoped validator caches per visitor,
  which is what the example above assumes. A singleton shares one cache across everyone, which
  is a correctness question before it is a performance one.
- **Keep the window short.** It exists to swallow a duplicate seconds old at most, not to stand
  in for a data cache with its own invalidation story.

One more run is opt-in. `FormidableOptions.TrackFormValidity` probes the whole model under
`SubmitProfile` on every field change — or once per window when `LiveDebounce` is set, at the
same cadence as the live pass it rides alongside — so a form with it switched on runs that same
draft rule three times per post-submit edit rather than twice.

## Which fields show "checking…"

A spinner in the wrong place teaches the wrong thing. Light the whole form up for one field's
uniqueness check and nobody can tell which answer they're waiting on; light nothing at all and
the input simply looks idle for half a second.

Two flags answer "is something still checking?", at different scopes. The engine-level
`IsValidating` (`IFormValidationEngine.IsValidating`) is true whenever *any* pass is in flight,
regardless of which field triggered it — the right one for a form-wide spinner.
`GetFieldState(field).IsValidating` is narrower, scoped to the fields the pass actually
concerns:

- **During a live pass**, it's true only for the field whose change started that pass, so
  `Username` and `DisplayName` — two independent async rules on the same form — each show their
  own "checking…" without one lighting up the other's.
- **During the debounced refresh**, it's true only for the fields edited within that debounce
  window, the ones whose changes scheduled it, even though the refresh itself re-validates the
  whole model in one pass.
- **When a live pass supersedes an in-flight refresh**, the live pass takes the indicator scope
  with it, the same way one live pass already displaces another's before submit.
- **During a submit**, it goes form-wide too: true for every field, because a submit really does
  (re-)check every field at once.

The same per-field flag drives the `Pending` CSS class (see [Options](options.md))
that ordinary `Validated*` inputs apply automatically.

**Sample:** [`/async`](../samples/Formidable.Sample/Pages/AsyncRules.razor)
