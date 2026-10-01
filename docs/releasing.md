# Releasing

This is the maintainer runbook for publishing Formidable, Formidable.Blazor, and
Formidable.AspNetCore to NuGet. It documents what the automated `Release` workflow actually does
(not an aspirational description) so a release stays reproducible without depending on anyone's
memory of the steps.

## Prerequisites (one-time)

- The repository lives at `github.com/xfunc-pty-ltd/formidable`. `RepositoryUrl` and
  `PackageProjectUrl` in `src/Directory.Build.props` point there.
- A deployment environment named `nuget-org` exists (GitHub repo → Settings → Environments → New
  environment), with required reviewers on it. The `publish` job in `.github/workflows/release.yml`
  declares `environment: nuget-org`, so this is what turns a pushed tag into a request to publish
  rather than the act of publishing.
- A Trusted Publishing policy exists on nuget.org (account menu → Trusted Publishing), naming
  repository owner `xfunc-pty-ltd`, repository `formidable`, workflow file `release.yml`, and
  environment `nuget-org`, scoped to new packages and versions under the glob `Formidable*`. The
  policy is created with the `xfunc-pty-ltd` organisation as owner, so the packages land under the
  organisation. A freshly created policy can sit "temporarily active" for only seven days, so
  create or re-arm it close to the release rather than long in advance.
- An environment secret named `NUGET_USER` is configured **on the `nuget-org` environment**
  (Settings → Environments → `nuget-org` → Environment secrets), holding the nuget.org username the
  policy above belongs to (nuget.org's own caution: the profile name, not an email address). The
  `publish` job exchanges this for a one-hour publish key via OIDC, so nothing long-lived is stored.

All of this is on you: the workflow can't do any of it, and it all needs to be in place before the
first tag is pushed. Until the environment exists the `publish` job does not start at all, which is
the failure direction to want: the workflow cannot publish by accident, only by arrangement.

## Pre-release verification

Run this on the commit you are about to tag. The release workflow builds and tests too, but it never
sets `FORMIDABLE_E2E`, so the browser suite in `tests/Formidable.Sample.E2E` sits out there. This
local run is the only thing that actually exercises it.

1. **Build the solution.** The E2E fixture starts every server it needs with `--no-build`, so it
   runs against whatever the Debug output already holds; a stale or missing build is the usual cause
   of a start-up timeout:

   ```bash
   dotnet build
   ```

2. **Install Chromium** — once per machine, not once per release. `pwsh` is PowerShell 7 (PowerShell
   Core), not the Windows-builtin `powershell.exe` (5.1). Install it from
   [github.com/PowerShell/PowerShell](https://github.com/PowerShell/PowerShell) if `pwsh` isn't
   already on the machine; Playwright's install script is a `.ps1` and needs it regardless of
   platform:

   ```bash
   pwsh tests/Formidable.Sample.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
   ```

3. **Run the whole suite with the browser tests switched on.** The fixture owns three ports (5180
   API, 5181 sample, 5183 Web App host fixture), so stop any sample app you have running first:

   ```bash
   FORMIDABLE_E2E=1 dotnet test
   ```

   In PowerShell, set the variable first: `$env:FORMIDABLE_E2E = "1"; dotnet test`.

   > [!IMPORTANT]
   > Everything must pass with nothing skipped except the docs-capture utilities (`DocsCapture`),
   > which stay behind their own `FORMIDABLE_CAPTURE=1` gate above this one and sit out of this run
   > on purpose: they render the PNGs under `docs/assets`, not verify anything, and
   > `FORMIDABLE_E2E=1 FORMIDABLE_CAPTURE=1 dotnet test --filter DocsCapture` is how you re-run them
   > after a visual change. Any other skip here means the variable never reached the test host and
   > the browser suite did not actually run: without it the same command still reports success,
   > having skipped every E2E test.

   `$env:FORMIDABLE_E2E` set this way persists for the rest of the PowerShell session, not just this
   command. Clear it once verification is done so a later plain `dotnet test` in the same session
   doesn't unexpectedly try to run the gated suite again: `Remove-Item Env:FORMIDABLE_E2E`.

4. **Walk the sample by eye.** The [manual checklist](../samples/MANUAL-CHECKLIST.md) is the pass a
   headless browser cannot do for you: colour, contrast, spacing, focus cues and native-control
   chrome, in both light and dark OS colour schemes.

## How a release ships

Versions are not set by hand anywhere in the source tree.
[MinVer](https://github.com/adamralph/minver) derives the package version from the nearest git tag
reachable from the commit being built, and `src/Directory.Build.props` sets `MinVerTagPrefix` to `v`.
So MinVer looks for tags shaped `v<version>`, not bare `<version>`.

In the commands below, `X.Y.Z` stands for the version you are releasing.

1. **Regenerate `CHANGELOG.md`** on `main`, in the commit you're about to tag:

   ```bash
   git cliff --tag vX.Y.Z -o CHANGELOG.md
   ```

   (git-cliff is a one-time machine install, like `pwsh` above; `cliff.toml` in the repo root holds
   the format.) Review the diff, then commit it on its own as `docs: release X.Y.Z` before tagging:
   the tag then points at a commit whose CHANGELOG already reads correctly for the version it's
   tagging. Keep that exact subject. `cliff.toml` leaves commits shaped `docs: release <version>` out
   of the changelog, so the next regeneration doesn't list the release commit under the release it
   published. Push it and let CI finish before you tag.

2. **Tag the release commit** on `main` with an annotated tag that names the release:

   ```bash
   git checkout main
   git pull
   git tag -a vX.Y.Z -m "Formidable X.Y.Z"
   ```

3. **Push the tag** — this is the trigger; nothing publishes on push to `main` itself:

   ```bash
   git push origin vX.Y.Z
   ```

4. **The `Release` workflow queues** (`.github/workflows/release.yml`), triggered by
   `push: tags: ['v*']`. It queues rather than runs: the single `publish` job declares
   `environment: nuget-org`, so it waits on that environment's reviewers before its first step
   executes. Pushing the tag asks for a release; approving the run is what releases. Nothing in
   the job has run when approval is given, so the reviewer approves on the strength of the local
   verification above and the commit being tagged, not the workflow's own build and test
   results.

   Once approved, step by step it:
   - Checks out the repository with `fetch-depth: 0`. MinVer needs the full tag history, not a
     shallow clone, to find the tag and compute the commit height from it.
   - Installs the .NET 10 SDK.
   - Runs `dotnet build -c Release -p:ContinuousIntegrationBuild=true`: a full Release build of
     the whole solution, with the property stamping it as a CI build so the packed assemblies
     carry deterministic-build metadata.
   - Runs `dotnet test -c Release --no-build`: the full test suite against that same build (no
     rebuild, so what's tested is exactly what gets packed next).
   - Packs all three shipping projects into `artifacts/`, each with `--no-build` (reusing the
     already-verified build output):
     - `dotnet pack src/Formidable -c Release --no-build -o artifacts`
     - `dotnet pack src/Formidable.Blazor -c Release --no-build -o artifacts`
     - `dotnet pack src/Formidable.AspNetCore -c Release --no-build -o artifacts`
   - Logs in to nuget.org via OIDC (`NuGet/login`), exchanging the `NUGET_USER` environment
     secret for a publish key valid one hour.
   - Pushes every `.nupkg` in `artifacts/` to nuget.org in one call:
     `dotnet nuget push "artifacts/*.nupkg" --api-key ${{ steps.login.outputs.NUGET_API_KEY }}
     --source https://api.nuget.org/v3/index.json --skip-duplicate`, holding the key the login
     step just produced. `--skip-duplicate` makes the push step safe to re-run (e.g. after a
     transient failure): it skips any package+version already on nuget.org instead of failing
     the whole job.

   If any step fails (build, test, or a pack) the workflow stops before the push step runs, so a
   failing test suite can never publish a package.

5. All three packages publish with the **same version number**, because MinVer resolves the same tag
   for every project in the solution: there's no scenario where `Formidable` and
   `Formidable.Blazor` ship at different versions from the same release.

### What CI does NOT do

`.github/workflows/ci.yml` runs `dotnet build -c Release` + `dotnet test -c Release --no-build` on
every push to `main` and on every pull request. Two more jobs run on the same triggers.
`hosted-demo-smoke` publishes the sample with `-p:HostedDemo=true` (a build smoke whose output
goes nowhere) and fails if that output holds a MudBlazor assembly, since the hosted demo leaves
MudBlazor out.

`consumer-restore` packs the three packages to a throwaway local feed and restores
them into two new consumers that between them pin both framework floors, so a raised floor fails
there. A fourth job, `commit-style`, checks commit subjects on pull requests (Dependabot's
excepted).

No `ci.yml` job pushes to NuGet. Publishing is tag-driven only, via the separate `Release`
workflow above: merging to `main` never ships a package by itself, and neither does a tag, which
only asks.

## Local dry run

Do this before pushing a tag you're not fully sure about, or any time you just want to sanity-check
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

Between tags, MinVer versions a commit from the nearest `v*` tag reachable from it plus the number
of commits since that tag. After a release tag `vX.Y.Z`, the shape is
`X.Y.<Z+1>-alpha.0.<height>`, where `<height>` is that commit count.

That pre-release version is expected for a dry run and is not a sign anything is misconfigured. A
local `pack` of `main` produces a version of that shape, and the `ci.yml` builds use one too,
because their checkout fetches the tags. With no reachable tag at all, MinVer falls back to
`0.0.0-alpha.0.<height>` (`<height>` is the commit count since the repo's start).

To dry-run what a *specific* tag would actually produce, create it locally, pack, then delete it
(nothing about MinVer requires the tag to be pushed to resolve it):

```bash
git tag vX.Y.Z
dotnet pack src/Formidable -c Release --no-build -o /tmp/formidable-dry-run
git tag -d vX.Y.Z
```

To inspect a package's contents without extracting it by hand — a `.nupkg` is a zip file, so any
zip-aware listing works, for example:

```bash
unzip -l /tmp/formidable-dry-run/Formidable.X.Y.Z.nupkg
```

Confirm `README.md` is present at the package root and that the `.nuspec` inside reports the version
you expected. The packed `README.md` is `docs/nuget-readme.md` under a new name: the
`<None Include="$(MSBuildThisFileDirectory)..\docs\nuget-readme.md" Pack="true"
PackagePath="README.md" />` item in `src/Directory.Build.props`, shared by all three projects,
renames it at pack time. The repo-root `README.md` is deliberately not packed: its raw HTML renders
on GitHub but not on nuget.org's HTML-free markdown subset.

## Post-release checklist

After the workflow's push step succeeds:

- [ ] Confirm all three packages appear on nuget.org at the expected version: search
      [`Formidable`](https://www.nuget.org/packages/Formidable),
      [`Formidable.Blazor`](https://www.nuget.org/packages/Formidable.Blazor), and
      [`Formidable.AspNetCore`](https://www.nuget.org/packages/Formidable.AspNetCore). New packages
      and new versions of existing packages can take a few minutes to finish indexing before they're
      visible in search: a listing page returning 404, or still showing the previous version,
      immediately after the workflow finishes is not necessarily a failure.
- [ ] Open each package's nuget.org page and confirm the README tab renders correctly: the tab
      shows `docs/nuget-readme.md`, the one file all three packages ship as their `README.md` (see
      above); a rendering problem here is a packaging bug worth fixing before the next release, not
      a nuget.org issue.
- [ ] Install into a disposable scratch project and confirm it restores at the released version:

  ```bash
  dotnet new console -o /tmp/formidable-scratch
  cd /tmp/formidable-scratch
  dotnet add package Formidable.Blazor --version X.Y.Z
  dotnet restore
  ```

- [ ] Spot-check that the installed package's dependency versions match what's declared in the
      relevant `.csproj` (e.g. `Formidable.Blazor` pulling in the matching `Formidable` version, not
      an older one already on nuget.org from a previous release).
- [ ] Create the GitHub Release on the tag, titled `Formidable X.Y.Z`, with the version's section of
      `CHANGELOG.md` as its body. Ticking "Create a discussion for this release" and picking the
      Announcements category also posts it to Discussions.

## If something goes wrong mid-release

- **The run appears but the `publish` job never starts**: that is the environment gate, not a stuck
  runner. Either it is waiting for a reviewer to approve, or the `nuget-org` environment doesn't
  exist yet (see Prerequisites). Approving is the release; declining or leaving it costs nothing,
  since no step has run.
- **Build or test fails in the workflow**: nothing was pushed (the push step never ran). Fix the
  issue on `main`, then re-tag and re-push: either move the tag to the fixed commit (`git tag -f`,
  `git push --force origin <tag>`, generally discouraged once a tag might have been observed by
  anyone else) or, simpler, bump to the next patch version and tag again.
- **Push step fails after some packages already went through**: re-running the workflow (or
  re-running just the push command locally with the same `artifacts/` output) is safe.
  `--skip-duplicate` means already-published packages are skipped rather than erroring the whole
  run.
- **Wrong version published**: NuGet does not allow re-publishing the same package+version with
  different contents. Unlist the bad version from the nuget.org UI and ship a new, corrected version
  instead.
