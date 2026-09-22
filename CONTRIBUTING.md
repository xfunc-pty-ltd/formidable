# Contributing to Formidable

Formidable is a single-maintainer project. That's a statement about capacity, not about
wanting fewer contributors — bug reports, doc fixes, and well-scoped PRs are all welcome — but
it does mean review bandwidth is limited and the bar for merging is "this fits the project's
shape," not just "this works."

## Building

You'll need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet build
dotnet test
```

Both should be clean before you open a PR: the build has `TreatWarningsAsErrors` enabled
(`Directory.Build.props`), and all three packages (`Formidable`, `Formidable.AspNetCore`,
`Formidable.Blazor`) require XML doc comments on public API (`GenerateDocumentationFile`,
`src/Directory.Build.props`) — a missing doc comment on a new public member is a build warning,
which is also a build error. CI (`.github/workflows/ci.yml`) runs the same two commands on every
push and pull request.

## Manually exercising a change

The sample app is the manual test bed — one page per feature, each a small complete form. From
the repo root, in two terminals:

```bash
dotnet run --project samples/Formidable.Sample.Api
dotnet run --project samples/Formidable.Sample
```

The API listens on `http://localhost:5180`; the Blazor app is at `http://localhost:5181`. If
your change touches the engine, the component kit, or the server adapters, find the sample page
that already exercises that area (see the README's [Learn more](README.md#learn-more) table for
which doc — and therefore which sample route — covers what) and check it by hand before opening
a PR, even if the automated tests pass.

## Commit style

Commits follow [Conventional Commits](https://www.conventionalcommits.org/) —
`feat: ...`, `fix: ...`, `docs: ...`, `test: ...`, `refactor: ...` — with a body explaining the
*why*, not just the *what*. Look at the existing git history for the tone and level of detail
expected.

## Before opening a PR

This library has a small, deliberate API surface on purpose (see the design goals in the
README) — it is easier to add a member than to remove one once it ships. **Open an issue to
discuss a new feature or a public API change before writing the PR.** Bug fixes, doc
corrections, and test improvements don't need that step; open the PR directly.

## Code of conduct

Be respectful and assume good faith. Anything else gets moderated at the maintainer's
discretion.
