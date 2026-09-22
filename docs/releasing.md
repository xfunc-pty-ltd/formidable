# Releasing

This is the maintainer runbook for publishing Formidable, Formidable.Blazor, and
Formidable.AspNetCore to NuGet. It documents what the automated `Release` workflow actually
does — not an aspirational description — so the release is reproducible without carrying the
steps around as tribal knowledge.

## Prerequisites (one-time)

- The repository is pushed to `github.com/xfunc/formidable` (`RepositoryUrl` /
  `PackageProjectUrl` in `src/Directory.Build.props` already point there; the git remote itself
  is a separate, manual step this repo hasn't taken yet).
- A `NUGET_API_KEY` repository secret is configured (GitHub repo → Settings → Secrets and
  variables → Actions → New repository secret). Generate the key at
  [nuget.org](https://www.nuget.org/) → your account → API Keys, scoped to push new packages
  and package versions for `Formidable`, `Formidable.Blazor`, and `Formidable.AspNetCore` (or a
  glob covering all three, e.g. `Formidable*`).

Neither step is something the release workflow can do for you — both must be in place before
the first tag is pushed.

## How a release ships

Versions are not set by hand anywhere in the source tree. [MinVer](https://github.com/adamralph/minver)
derives the package version from the nearest git tag reachable from the commit being built, and
`src/Directory.Build.props` sets `MinVerTagPrefix` to `v` — so MinVer looks for tags shaped
`v<version>`, not bare `<version>`.

1. **Tag the release commit** on `main`, using the `v0.x.y-preview.N` shape while the project is
   pre-1.0 (adjust `0.x.y` and drop `-preview.N` once the project leaves preview):

   ```bash
   git checkout main
   git pull
   git tag v0.1.0-preview.1
   ```

2. **Push the tag** — this is the trigger; nothing publishes on push to `main` itself:

   ```bash
   git push origin v0.1.0-preview.1
   ```

3. **The `Release` workflow runs** (`.github/workflows/release.yml`), triggered by `push: tags:
   ['v*']`. Step by step, it:
   - Checks out the repository with `fetch-depth: 0` — MinVer needs the full tag history, not a
     shallow clone, to find the tag and compute the commit height from it.
   - Installs the .NET 10 SDK.
   - Runs `dotnet build -c Release` — a full Release build of the whole solution.
   - Runs `dotnet test -c Release --no-build` — the full test suite against that same build (no
     rebuild, so what's tested is exactly what gets packed next).
   - Packs all three shipping projects into `artifacts/`, each with `--no-build` (reusing the
     already-verified build output):
     - `dotnet pack src/Formidable -c Release --no-build -o artifacts`
     - `dotnet pack src/Formidable.Blazor -c Release --no-build -o artifacts`
     - `dotnet pack src/Formidable.AspNetCore -c Release --no-build -o artifacts`
   - Pushes every `.nupkg` in `artifacts/` to nuget.org in one call:
     `dotnet nuget push "artifacts/*.nupkg" --api-key ${{ secrets.NUGET_API_KEY }} --source
     https://api.nuget.org/v3/index.json --skip-duplicates`. `--skip-duplicates` makes the push
     step safe to re-run (e.g. after a transient failure) — it skips any package+version already
     on nuget.org instead of failing the whole job.

   If any step fails — build, test, or a pack — the workflow stops before the push step runs, so
   a failing test suite can never publish a package.

4. All three packages publish with the **same version number**, because MinVer resolves the same
   tag for every project in the solution — there's no scenario where `Formidable` and
   `Formidable.Blazor` ship at different versions from the same release.

### What CI does NOT do

`.github/workflows/ci.yml` runs `dotnet build -c Release` + `dotnet test -c Release --no-build`
on every push to `main` and on every pull request. It never packs and never pushes to NuGet.
Publishing is tag-driven only, via the separate `Release` workflow above — merging to `main`
never ships a package by itself.

## Local dry run

Do this before pushing a tag you're not fully sure about, or any time you want to sanity-check
what a given commit would publish as. It packs to a throwaway directory outside the repo (or a
gitignored one you delete afterward) using the exact same `--no-build` pack invocations as the
workflow, so what you're inspecting matches production behavior.

```bash
dotnet build -c Release
dotnet test -c Release --no-build

mkdir -p /tmp/formidable-dry-run
dotnet pack src/Formidable -c Release --no-build -o /tmp/formidable-dry-run
dotnet pack src/Formidable.Blazor -c Release --no-build -o /tmp/formidable-dry-run
dotnet pack src/Formidable.AspNetCore -c Release --no-build -o /tmp/formidable-dry-run

ls /tmp/formidable-dry-run
```

Without a tag reachable from the current commit, MinVer falls back to a `0.0.0-alpha.0.<height>`
version (`<height>` is the commit count since the repo's start) — that's expected for a
pre-release dry run and is how every ordinary local `pack` and the `ci.yml` build behave; it is
not a sign anything is misconfigured. To dry-run what a *specific* tag would actually produce,
create it locally, pack, then delete it — nothing about MinVer requires the tag to be pushed to
resolve it:

```bash
git tag v0.1.0-preview.1
dotnet pack src/Formidable -c Release --no-build -o /tmp/formidable-dry-run
git tag -d v0.1.0-preview.1
```

To inspect a package's contents without extracting it by hand — a `.nupkg` is a zip file, so any
zip-aware listing works, for example:

```bash
unzip -l /tmp/formidable-dry-run/Formidable.0.1.0-preview.1.nupkg
```

Confirm `README.md` is present at the package root (packed via the `<None Include=
"..\README.md" Pack="true" PackagePath="\" />` item in `src/Directory.Build.props`, shared by
all three projects) and that the `.nuspec` inside reports the version you expected.

## Post-release checklist

After the workflow's push step succeeds:

- [ ] Confirm all three packages appear on nuget.org at the expected version — search
      [`Formidable`](https://www.nuget.org/packages/Formidable),
      [`Formidable.Blazor`](https://www.nuget.org/packages/Formidable.Blazor), and
      [`Formidable.AspNetCore`](https://www.nuget.org/packages/Formidable.AspNetCore). New
      packages and new versions of existing packages can take a few minutes to finish indexing
      before they're visible in search — a listing page returning 404 immediately after the
      workflow finishes is not necessarily a failure.
- [ ] Open each package's nuget.org page and confirm the README tab renders correctly — this is
      the same `README.md` all three ship (see above); a rendering problem here is a packaging
      bug worth fixing before the next release, not a nuget.org issue.
- [ ] Install into a disposable scratch project and confirm it restores at the released version:

  ```bash
  dotnet new console -o /tmp/formidable-scratch
  cd /tmp/formidable-scratch
  dotnet add package Formidable.Blazor --version 0.1.0-preview.1
  dotnet restore
  ```

- [ ] Spot-check that the installed package's dependency versions match what's declared in the
      relevant `.csproj` (e.g. `Formidable.Blazor` pulling in the matching `Formidable` version,
      not an older one already on nuget.org from a previous release).

## If something goes wrong mid-release

- **Build or test fails in the workflow**: nothing was pushed (the push step never ran). Fix the
  issue on `main`, then re-tag and re-push — either move the tag to the fixed commit (`git tag
  -f`, `git push --force origin <tag>`, generally discouraged once a tag might have been
  observed by anyone else) or, simpler, bump to the next `-preview.N` and tag again.
- **Push step fails after some packages already went through**: re-running the workflow (or
  re-running just the push command locally with the same `artifacts/` output) is safe —
  `--skip-duplicates` means already-published packages are skipped rather than erroring the
  whole run.
- **Wrong version published**: NuGet does not allow re-publishing the same package+version with
  different contents. Unlist the bad version from the nuget.org UI and ship a new, corrected
  version instead.
