# Contributing to Formidable

Formidable is a single-maintainer project. That's a statement about capacity, not about
wanting fewer contributors — bug reports, doc fixes, and well-scoped PRs are all welcome — but
it does mean review bandwidth is limited and the bar for merging is "this fits the project's
shape," not just "this works."

## Project layout

```
src/
  Formidable/              core: profiles, validator seams, introspection (FluentValidation only)
  Formidable.Blazor/       engine + headless component kit
  Formidable.AspNetCore/   endpoint/action filters + ProblemDetails
samples/
  Formidable.Sample/       the WASM sample app (one page per feature)
  Formidable.Sample.Api/   the sample's server, Minimal API + MVC
  Formidable.Sample.Shared/  models and validators shared by both, references Formidable only
tests/
  Formidable.Tests/               core unit tests
  Formidable.Blazor.Tests/        bUnit component/engine tests
  Formidable.AspNetCore.Tests/    server-adapter tests
  Formidable.Sample.E2E/          Playwright browser tests, env-gated (see below)
docs/                      the reference docs, one file per topic
```

`Formidable` has no Blazor or ASP.NET Core dependency; `Formidable.Blazor` and
`Formidable.AspNetCore` both depend only on `Formidable`, never on each other.

## Dev setup

You'll need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet build
dotnet test
```

Both should be clean before you open a PR: the build has `TreatWarningsAsErrors` enabled
(`Directory.Build.props`), and all three shipping packages require XML doc comments on public
API (`GenerateDocumentationFile`, `src/Directory.Build.props`) — a missing doc comment on a new
public member is a build warning, which is also a build error. Plain `dotnet test` runs the
unit suite only; it also reports the E2E project's tests as skipped (see below) rather than
failing, so a clean run reports passed + skipped, never a failure, when nothing is broken. CI
(`.github/workflows/ci.yml`) runs `dotnet build -c Release` and `dotnet test -c Release
--no-build` on every push and pull request.

### The E2E gate

`tests/Formidable.Sample.E2E` drives the sample app through a real headless browser
(Playwright). Every test there self-skips unless the `FORMIDABLE_E2E` environment variable is
set — CI never sets it, so these tests don't run there. If your change touches the sample app,
the component kit's rendered output, or anything else a browser test would catch, run this
suite locally before opening a PR:

```bash
dotnet build
pwsh tests/Formidable.Sample.E2E/bin/Debug/net10.0/playwright.ps1 install chromium   # once per machine
FORMIDABLE_E2E=1 dotnet test
```

In PowerShell, set the variable first: `$env:FORMIDABLE_E2E = "1"; dotnet test`. The E2E
fixture starts both sample servers itself (killing them on teardown), so stop any sample app
you already have running first, and expect nothing skipped except the three docs-capture
utilities. [Releasing](docs/releasing.md) has the full pre-release version of this same
checklist.

### Manually exercising a change

The sample app is the manual test bed — one page per feature, each a small complete form. From
the repo root, in two terminals:

```bash
dotnet run --project samples/Formidable.Sample.Api
dotnet run --project samples/Formidable.Sample
```

The API listens on `http://localhost:5180`; the Blazor app is at `http://localhost:5181`. If
your change touches the engine, the component kit, or the server adapters, find the sample page
that already exercises that area (see the README's [Learn more](README.md#documentation) table for
which doc — and therefore which sample route — covers what) and check it by hand before opening
a PR, even if the automated tests pass. The [manual checklist](samples/MANUAL-CHECKLIST.md)
is the committed walkthrough checklist covering every sample page in both light and dark OS
colour schemes; [Recipes](docs/recipes.md) is a task-oriented "I want to…" index if
you're trying to find where a particular behaviour lives before changing it.

## Commit conventions

Commits follow [Angular Conventional Commits](https://www.conventionalcommits.org/) —
`feat: ...`, `fix: ...`, `docs: ...`, `test: ...`, `refactor: ...` — with a body explaining the
*why*, not just the *what*. Look at the existing git history for the tone and level of detail
expected.

Where the change lands in one of the surfaces below, name it as an optional scope,
`type(scope): ...`:

`core` (src/Formidable), `blazor` (src/Formidable.Blazor), `aspnetcore`
(src/Formidable.AspNetCore), `sample` (everything under samples/), `e2e`
(tests/Formidable.Sample.E2E), `docs` (docs/ + README + markdown corpus), `build`
(solution/csproj/workflows/packaging). Rules: one scope per commit; omit when genuinely
cross-cutting or when the type already names the surface (plain `docs:` for docs-corpus work);
unit tests take the scope of the code under test (`test(blazor):`).

## Before opening a PR

This library has a small, deliberate API surface on purpose (see the design goals in the
README) — it is easier to add a member than to remove one once it ships. **Open an issue to
discuss a new feature or a public API change before writing the PR.** Bug fixes, doc
corrections, and test improvements don't need that step; open the PR directly.

A PR is ready for review once:

- `dotnet test` is green (and `FORMIDABLE_E2E=1 dotnet test` too, if the change could plausibly
  affect the sample app or component kit output).
- `dotnet build -c Release` produces zero warnings.
- Any doc claiming a specific behaviour still matches that behaviour, and any `Source:` /
  `Excerpt from` snippet in `docs/` or `README.md` still matches the file it quotes.
- The PR description says what changed and why, and calls out any docs it touches.

## Code of conduct

Be respectful and assume good faith. Anything else gets moderated at the maintainer's
discretion.
