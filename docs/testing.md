# Testing

The suite has two tiers: an unconditional unit/component tier that runs on every `dotnet test`
and every CI build, and a browser tier that only runs when you ask for it. This doc is a map of
both — what each layer covers, how to run it, and where the gate that combines them lives. See
[`CONTRIBUTING.md`](../CONTRIBUTING.md) for dev setup and PR expectations, and
[`docs/recipes.md`](recipes.md) for a task-oriented index of *behaviour* rather than tests.

## Unit and component tests

Three projects, unconditional — no environment variable, no running server, no browser:

| Project | Count | Covers |
|---|---|---|
| `tests/Formidable.Tests` | 71 | Core: `Formidable` — profiles (Draft/Submit, `ProfiledValidator<T>`), the `IModelValidator<T>` seam, the reflection-based introspector, path resolution, service registration. |
| `tests/Formidable.AspNetCore.Tests` | 21 | `Formidable.AspNetCore` — the minimal-API endpoint filter, the MVC `[Validate]` action filter, and the `ValidationProblemDetails`/warnings wire mapping. |
| `tests/Formidable.Blazor.Tests` | 108 | `Formidable.Blazor` — `FormValidationEngine` (live/submit/refresh passes, supersession and race behaviour, server-issue apply/replace), the component kit (bUnit-rendered), the focus service, and the field registry. |

Run the whole tier from the repo root:

```bash
dotnet test
```

A clean run reports **200 passed + 30 skipped** — the 30 are the 27 browser tests below plus
three docs-capture utilities that render the screenshots under `docs/assets` (see both below),
reported as skipped rather than failing so their absence never looks like a broken build. To run
just one
project (useful while iterating), point `dotnet test` at its `.csproj` or use `--filter`:

```bash
dotnet test tests/Formidable.Blazor.Tests
```

Both `dotnet build` and `dotnet test` should be clean before opening a PR — see
[`CONTRIBUTING.md`](../CONTRIBUTING.md) for the warnings-as-errors and XML-doc requirements that
make a "clean" build stricter than it looks.

## Browser tests (env-gated)

`tests/Formidable.Sample.E2E` drives the actual sample app through headless Chromium
(Microsoft.Playwright) — real navigation, real form interaction, real focus and DOM assertions,
nothing bUnit's simulated renderer can stand in for. Every test in the project self-skips unless
the `FORMIDABLE_E2E` environment variable is set, so an ordinary `dotnet test` never launches a
browser or the sample servers. Its `SampleAppFixture` owns both: it starts
`Formidable.Sample.Api` and `Formidable.Sample` itself (`--no-build`, so it needs a build already
on disk), waits for both to answer, and tears down the whole process tree afterward.

27 tests across four files:

- One navigation smoke test that walks the sidebar itself.
- One smoke test per sample page (18) — the page loads, its form renders, nothing throws.
- Eight tests going deep on `/workout`, the composite page that exercises every feature at once —
  blocked submit and every summary-entry kind landing (native input, collection fieldset, wrapped
  input, the disclosure gate), attendee add/remove and per-item rules, async pending state,
  server-applied coupon apply/replace, and a regression pin for a fixed engine race.

A fifth file, `DocsCapture.cs`, holds three docs-capture utilities that regenerate the PNGs under
`docs/assets` — they sit behind their own `FORMIDABLE_CAPTURE=1` gate, on top of `FORMIDABLE_E2E`,
so an ordinary gated run never rewrites the shipped images as a side effect (see the NOTE below).

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
> A clean gated run reports **227 passed + 3 skipped** — nothing skipped except the docs-capture
> utilities (`DocsCapture.cs`), which stay behind their own `FORMIDABLE_CAPTURE=1` gate on top of
> this one and sit out of this run on purpose: they render the PNGs under `docs/assets`, not
> verify anything. Any other skip here means the variable never reached the test host, not that
> the suite is somehow smaller.

## The release gate

[`docs/releasing.md`](releasing.md)'s **Pre-release verification** section is where both tiers
above, plus a manual pass, combine into the actual checklist a maintainer runs before tagging a
release — build, gated `dotnet test`, and the walkthrough below, in that order. Run it on the
commit you're about to tag, not just once at the start of a change.

## The manual checklist

Some things a browser driven by a test script can't judge: colour and contrast, spacing, focus
outlines, and native control chrome (date pickers, `<select>` dropdowns) rendering correctly in
both light and dark OS colour schemes. [`samples/MANUAL-CHECKLIST.md`](../samples/MANUAL-CHECKLIST.md)
is the committed walkthrough for that eyes-on pass — one section per sample-page group, matching
the sidebar, plus a navigation and a light/dark pass. It's the pre-release human gate the two
automated tiers above can't replace, not a substitute for either of them.
