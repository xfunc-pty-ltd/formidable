# Testing

**You should already know:** how to wire up a form end to end
([Quickstart](quickstart.md)) — this page assumes you already have something worth
testing, not how to build it.

A suite you can't run in ten seconds is a suite that stops getting run. Make every test spin up
two servers and a real browser, and `dotnet test` turns from a save-time reflex into a coffee
break — so the checks that would catch a blocked submit, a focus miss, or a dark-mode CSS token
get skipped in practice, not on purpose. Formidable's suite draws that line deliberately instead:
an unconditional tier fast enough to run on every save, and a browser tier real enough to catch
what a simulated renderer can't, switched on only when asked. Even that doesn't cover
everything — colour, contrast, and native control chrome still want a human's eyes, which is
what the checklist at the end of this page is for.

## Need to know

Two commands cover almost everything:

```bash
dotnet test                    # unconditional tier
FORMIDABLE_E2E=1 dotnet test   # adds the browser tier
```

The first is what CI and every PR run. The second is what a maintainer runs by hand before
tagging a release — see [The release gate](#the-release-gate) below. Both should report zero
failures before you trust a change. A plain run also reports some tests as *skipped* rather than
run: that's expected, not a problem. Skipped there means the browser tier below, gated behind
`FORMIDABLE_E2E`, plus a handful of docs-capture utilities gated further behind
`FORMIDABLE_CAPTURE` (see [Browser tests](#browser-tests-env-gated)). What isn't expected is a
run reporting fewer tests than usual — that's a signal an environment variable or a build step
didn't do what it was supposed to, not that the suite shrank on its own.

Testing a form you built rather than the library itself? [Testing your forms](#testing-your-forms)
is the next section, and it's the only one written for that audience; everything after it is this
repo's own suite.

A PR is expected to pin new behaviour with a test at the tier that would actually exercise it —
unit/bUnit for engine and component logic, browser for anything only a real rendered page can
show — keep the plain suite green, and produce a clean Release build. See the
[contributing guide](../CONTRIBUTING.md) for dev setup and the full PR checklist, and
[Recipes](recipes.md) for a task-oriented index of *behaviour* rather than tests.

## Testing your forms

Everything else on this page is about Formidable's own suite. This section is the other audience:
the tests you write over a form you built with it. Two layers cover almost all of it — the rules
without a renderer, and the rendered form under bUnit — and the third thing worth knowing is how
to wait for an answer that arrives asynchronously.

### The rules, without Blazor

A validator is a plain FluentValidation class and a profile is a value you pass, so the rules
answer to a test with no renderer anywhere in it:

```csharp
var validator = new FluentValidationModelValidator<Brief>(new BriefValidator());
var blank = new Brief();

var draft = await validator.ValidateAsync(blank, ValidationProfile.Draft);
var submit = await validator.ValidateAsync(blank, ValidationProfile.Submit);

Assert.True(draft.IsValid);                             // a blank draft is fine
Assert.Contains(submit.Errors, i => i.Path == "Title"); // submitting it is not
```

`IModelValidator<T>` is the seam the engine validates through, so a test driving it directly runs
exactly what a form runs — one profile at a time, which is the pairing worth pinning: a presence
rule stays quiet under `Draft` and blocks under `Submit`. `ValidationReport` splits the answer by
severity (`Errors`, `Warnings`, `Infos`, and `Advisories` for the two non-error buckets together),
and `IsValid` counts errors only, so a warning-only report is valid. `ValidationIssue.Path` is
FluentValidation's own property path, indexes included (`Lines[0].Sku`).

All of that lives in the core `Formidable` package, which has no Blazor dependency — a plain xunit
project referencing it is enough. Resolve `IModelValidator<T>` from a container if the test already
has one; `new FluentValidationModelValidator<T>(...)` is the shortcut when it doesn't.

### The form, under bUnit

The kit renders under [bUnit](https://bunit.dev) like any other component set. Two of its services
talk to JavaScript, so a component test supplies its own stand-ins for them: register the doubles
*before* `AddFormidableBlazor()`, which respects registrations that are already there.

```csharp
public class SignupFormTests : BunitContext
{
    private readonly RecordingFocusService _focus = new();       // your recording doubles
    private readonly RecordingDomValueSync _domSync = new();

    public SignupFormTests()
    {
        Services.AddSingleton<IFormidableFocusService>(_focus);
        Services.AddSingleton<IFormidableDomValueSync>(_domSync);
        Services.AddFormidableBlazor();
        Services.AddSingleton<IValidator<Signup>>(new SignupValidator());
    }

    [Fact]
    public void Blocked_submit_shows_the_message_where_the_field_renders()
    {
        var cut = Render<SignupPage>();

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
            Assert.Contains("Name is required", cut.Find("ul.formidable-messages").TextContent));
    }
}
```

`IFormidableFocusService` is the one to register first: `FormidableSummary` injects it outright, so
a form rendering a summary needs *something* there. A recording double also makes focus assertable,
and which field a blocked submit moved to is worth pinning, since
`FocusFirstErrorOnInvalidSubmit` moves it on every blocked submit by default.
`IFormidableDomValueSync` matters as soon as a `FormidableInputNumber` or `FormidableInputDate` is
on the form: both inject it and call it on blur. Formidable's own
[`RecordingDomValueSync`](../tests/Formidable.Blazor.Tests/Fixtures/RecordingDomValueSync.cs) is a
twenty-line class recording every call, and a focus double is the same shape over `FocusAsync`.

There is an alternative to doubling the interfaces: let the real services run and stand in for the
JavaScript instead, with bUnit's
`JSInterop.SetupModule("./_content/Formidable.Blazor/formidable.js")`. That is what Formidable's
own `FocusServiceTests` do, because there the service *is* the thing under test. For a form test,
the interface doubles are less machinery.

### Waiting for the answer

A verdict lands a render or two after the event that asked for it, and an async rule lands later
still. Two habits cover it:

- **Assert through `WaitForAssertion`.** It retries until the assertion passes or the timeout ends,
  which is what makes a message that arrives one render later a pass rather than a race.
  `WaitForState` does the same for a predicate.
- **Call the form's own methods through `InvokeAsync`.** `SubmitAsync()`, `ResetAsync()` and
  `ApplyServerIssues(...)` all mutate validation state and trigger renders, so they belong on the
  renderer's synchronization context. Reach the component with `FindComponent`:
  `var form = cut.FindComponent<FormidableForm<Signup>>();` then
  `await form.InvokeAsync(() => form.Instance.SubmitAsync());`.

Pinning a *pending* state needs one more thing, because "checking…" is by definition gone by the
time the rule answers. Hold the rule open with a `TaskCompletionSource` the test controls: assert
`IsValidating` (engine-wide) or `GetFieldState(field).IsValidating` (field-scoped) while the gate
is closed, then complete it and wait for the verdict. Formidable's own engine tests use exactly
that gate, and [Async validation](async-validation.md) explains which scope each pass reports.

## Unit and component tests

Three projects, unconditional — no environment variable, no running server, no browser:

| Project | Covers |
|---|---|
| `tests/Formidable.Tests` | Core: `Formidable` — profiles (Draft/Submit, `ProfiledValidator<T>`), the `IModelValidator<T>` seam, the reflection-based introspector, path resolution, service registration. |
| `tests/Formidable.AspNetCore.Tests` | `Formidable.AspNetCore` — the minimal-API endpoint filter, the MVC `[Validate]` action filter, and the `ValidationProblemDetails`/advisories wire mapping. |
| `tests/Formidable.Blazor.Tests` | `Formidable.Blazor` — `FormValidationEngine` (live/submit/refresh passes, supersession and race behaviour, server-issue apply/replace), the component kit (bUnit-rendered), the focus service, and the field registry. |

The focus service implements both `IDisposable` and `IAsyncDisposable`, so a bUnit container
built through `AddFormidableBlazor()` tears down on ordinary synchronous dispose — nothing extra
to write. Awaiting `Services.DisposeAsync()` instead still works and stays the more thorough
choice, which is what Formidable's own suite does throughout.

Run the whole tier from the repo root:

```bash
dotnet test
```

A clean run also reports the browser tier below as skipped rather than failed — see
[Need to know](#need-to-know) for why that's expected. To run just one project (useful while
iterating), point `dotnet test` at its `.csproj` or use `--filter`:

```bash
dotnet test tests/Formidable.Blazor.Tests
```

Both `dotnet build` and `dotnet test` should be clean before opening a PR — see
the [contributing guide](../CONTRIBUTING.md) for the warnings-as-errors and XML-doc requirements that
make a "clean" build stricter than it looks.

## Browser tests (env-gated)

`tests/Formidable.Sample.E2E` drives the actual sample app through headless Chromium
(Microsoft.Playwright) — real navigation, real form interaction, real focus and DOM assertions,
nothing bUnit's simulated renderer can stand in for. Every test in the project self-skips unless
the `FORMIDABLE_E2E` environment variable is set, so an ordinary `dotnet test` never launches a
browser or the sample servers. Its `SampleAppFixture` owns both: it starts
`Formidable.Sample.Api` and `Formidable.Sample` itself (`--no-build`, so it needs a build already
on disk), waits for both to answer, and tears down the whole process tree afterward.

The tests fall into a few groups:

- A navigation smoke test that walks the sidebar itself.
- A render smoke per sample page — the page loads, its heading renders, nothing throws.
- A journey per behavior-bearing page, one file each under `Journeys/`, pinning that page's
  central lesson with at least one real-typed, real-blurred path — a blocked submit, a live pass,
  a suppression reveal, whatever the page teaches. `/workout`'s journeys are the deepest of these:
  blocked submit and every summary-entry kind landing (native input, collection fieldset, wrapped
  input, the disclosure gate), attendee add/remove and per-item rules, async pending state,
  server-applied coupon apply/replace, and a regression pin for a fixed engine race.
- A handful of keystroke-level pins (`InputRegressions.cs`) for input mechanics no smoke or
  journey drives deep enough to see — caret position mid-type, a date typed segment by segment, a
  number field's blur-time value sync.

`DocsCapture.cs` holds a further group: docs-capture utilities that regenerate the PNGs under
`docs/assets`. They sit behind their own `FORMIDABLE_CAPTURE=1` gate, on top of `FORMIDABLE_E2E`,
so an ordinary gated run never rewrites the shipped images as a side effect (see the note below).

Run it:

```bash
dotnet build
pwsh tests/Formidable.Sample.E2E/bin/Debug/net10.0/playwright.ps1 install chromium   # once per machine
FORMIDABLE_E2E=1 dotnet test
```

In PowerShell, set the variable first — `$env:FORMIDABLE_E2E = "1"; dotnet test` — and clear it
afterward (`Remove-Item Env:FORMIDABLE_E2E`) so it doesn't linger into a later plain run in the
same session. Stop any sample app you already have running first: the fixture owns ports 5180
and 5181 and a port already in use fails the run with an actionable error rather than a hang.

> [!NOTE]
> A gated run skips only the docs-capture utilities (`DocsCapture.cs`) — they stay behind their
> own `FORMIDABLE_CAPTURE=1` gate on top of this one and sit out of this run on purpose: they
> render the PNGs under `docs/assets`, not verify anything. Any other skip here means the
> variable never reached the test host, not that the suite is somehow smaller.

## The release gate

[Releasing](releasing.md)'s **Pre-release verification** section is where both tiers
above, plus a manual pass, combine into the actual checklist a maintainer runs before tagging a
release — build, gated `dotnet test`, and the walkthrough below, in that order. Run it on the
commit you're about to tag, not just once at the start of a change.

## The manual checklist

Some things a browser driven by a test script can't judge: colour and contrast, spacing, focus
outlines, and native control chrome (date pickers, `<select>` dropdowns) rendering correctly in
both light and dark OS colour schemes. The [manual checklist](../samples/MANUAL-CHECKLIST.md)
is the committed walkthrough for that eyes-on pass — one section per sample-page group, matching
the sidebar, plus a navigation and a light/dark pass. It's the pre-release human gate the two
automated tiers above can't replace, not a substitute for either of them.
