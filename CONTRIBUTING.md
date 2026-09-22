# Contributing to Formidable

Formidable is a single-maintainer project. That's a statement about capacity, not about
wanting fewer contributors (bug reports, doc fixes, and well-scoped PRs are all welcome), but
it does mean review bandwidth is limited and the bar for merging is "this fits the project's
shape," not just "this works."

Releases are the maintainer's alone: contributions land through pull requests, the maintainer
reviews and merges them, and nothing in a pull request can publish a package or move a tag.

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
API (`GenerateDocumentationFile`, `src/Directory.Build.props`). A missing doc comment on a new
public member is a build warning, which is also a build error. Plain `dotnet test` runs the
unit suite only; it also reports the E2E project's tests as skipped (see below) rather than
failing, so a clean run reports passed + skipped, never a failure, when nothing is broken. CI
(`.github/workflows/ci.yml`) runs `dotnet build -c Release` and `dotnet test -c Release
--no-build` on every push and pull request.

### The E2E gate

`tests/Formidable.Sample.E2E` drives the sample app through a real headless browser
(Playwright). Every test there self-skips unless the `FORMIDABLE_E2E` environment variable is
set. CI never sets it, so these tests don't run there. If your change touches the sample app,
the component kit's rendered output, or anything else a browser test would catch, run this
suite locally before opening a PR:

```bash
dotnet build
pwsh tests/Formidable.Sample.E2E/bin/Debug/net10.0/playwright.ps1 install chromium   # once per machine
FORMIDABLE_E2E=1 dotnet test
```

In PowerShell, set the variable first: `$env:FORMIDABLE_E2E = "1"; dotnet test`. The E2E
fixture starts every server it needs itself (killing them on teardown), so stop any sample app
you already have running first, and expect nothing skipped except the three docs-capture
utilities. [Releasing](docs/releasing.md) has the full pre-release version of this same
checklist.

### Manually exercising a change

The sample app is the manual test bed (one page per feature, each a small complete form). From
the repo root, in two terminals:

```bash
dotnet run --project samples/Formidable.Sample.Api
dotnet run --project samples/Formidable.Sample
```

The API listens on `http://localhost:5180`; the Blazor app is at `http://localhost:5181`. If
your change touches the engine, the component kit, or the server adapters, find the sample page
that already exercises that area (see the README's [Learn more](README.md#documentation) table for
which doc, and therefore which sample route, covers what) and check it by hand before opening
a PR, even if the automated tests pass. The [manual checklist](samples/MANUAL-CHECKLIST.md)
is the committed walkthrough checklist covering every sample page in both light and dark OS
colour schemes; [Recipes](docs/recipes.md) is a task-oriented "I want to…" index if
you're trying to find where a particular behaviour lives before changing it.

## How to contribute a change

Contributions follow GitHub's standard fork-and-pull-request flow. `main` is the only
long-lived branch: there is no develop branch, releases are tags on `main`, and pull requests
are squash-merged, so history stays linear whatever your fork looks like.

1. Fork the repository on GitHub and clone your fork.
2. Add this repository as `upstream`:
   `git remote add upstream https://github.com/xfunc-pty-ltd/formidable.git`.
3. Branch off `main` for one change (`fix/summary-focus`, `docs/quickstart-typo`).
4. Commit following [Commit conventions](#commit-conventions) below, and run the checks in
   [Before opening a PR](#before-opening-a-pr).
5. Push the branch to your fork and open a pull request against `xfunc-pty-ltd/formidable`'s `main`.
6. CI runs the build, the tests and the commit-shape check; the maintainer reviews and
   squash-merges. The squash subject is taken from the pull request title, so give the PR a
   title in the same `type(scope): subject` shape.
7. Keep your fork current by syncing its `main` (the Sync fork button on GitHub, or
   `git fetch upstream` followed by `git rebase upstream/main`), and rebase your branch on it
   rather than merging, so the PR stays a clean line.

## Commit conventions

Commits follow [Angular Conventional Commits](https://www.conventionalcommits.org/):
`type(scope): subject`, with types `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `style`,
`build`, `chore` or `ci`, a subject of at most 72 characters, and a body explaining the *why*,
not just the *what*. Reference an issue from the body (`Closes #123`); the changelog links it.
CI checks the shape on every pull request; Dependabot's own are exempt, since its titles are
machine-fixed. Look at the existing git history for the tone and level of detail expected.

Where the change lands in one of the surfaces below, name it as an optional scope,
`type(scope): ...`:

`core` (src/Formidable), `blazor` (src/Formidable.Blazor), `aspnetcore`
(src/Formidable.AspNetCore), `sample` (everything under samples/), `e2e`
(tests/Formidable.Sample.E2E), `docs` (docs/ + README + markdown corpus), `build`
(solution/csproj/workflows/packaging), `deps` (dependency bumps, Dependabot's own commits
included). Rules: one scope per commit; omit when genuinely cross-cutting or when the type
already names the surface (plain `docs:` for docs-corpus work); unit tests take the scope of
the code under test (`test(blazor):`).

### XML documentation

Write a `<summary>` as one sentence a reader can use from a tooltip alone: what the member does
for its caller, or, for a property, the noun it holds and its default. Keep it under 25 words
as a working limit; 40 is the hard gate. Add `<param>`, `<typeparam>`, `<returns>`, and
`<exception>` wherever the member has them, naming the condition an exception throws under, not
just its type. Save `<remarks>` for one thing the caller must act on; put the reasoning behind a
line of code in a `//` comment beside it instead. No em dashes. Describe the public surface in
terms a consumer already knows, not the engine's own internal vocabulary. The maintainer checks
every PR's XML for summary length, required tags, and that vocabulary boundary.

## Before opening a PR

This library has a small, deliberate API surface on purpose (see the design goals in the
README): it is easier to add a member than to remove one once it ships. **Open an issue to
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
