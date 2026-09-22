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

A PR is expected to pin new behaviour with a test at the tier that would actually exercise it —
unit/bUnit for engine and component logic, browser for anything only a real rendered page can
show — keep the plain suite green, and produce a clean Release build. See the
[contributing guide](../CONTRIBUTING.md) for dev setup and the full PR checklist, and
[Recipes](recipes.md) for a task-oriented index of *behaviour* rather than tests.

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
