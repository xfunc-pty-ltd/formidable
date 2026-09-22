#Requires -Version 7.0

<#
.SYNOPSIS
    Builds the GitHub Pages-ready hosted-demo artifact for the Formidable sample and serves it
    locally, so a review happens on the exact bits a deploy would ship before anything is ever
    published.

.DESCRIPTION
    Publishes samples/Formidable.Sample with -p:HostedDemo=true (Release), which compiles in the
    in-browser handler that answers the sample's API calls (see HostedDemoApiHandler.cs) - GitHub
    Pages hosts static files only, so the real API is not reachable from a deployed build.

    The published wwwroot is then made GitHub Pages-ready: <base href="/"> is rewritten to
    <base href="/formidable/"> (a project page is served under that path), index.html is copied
    to 404.html so a deep link falls back to the SPA shell, and .nojekyll is added so GitHub
    Pages serves the underscore-prefixed _framework folder unmodified.

    This script never pushes, deploys, or publishes anything - it only builds and, by default,
    serves the result locally for review.

.PARAMETER NoServe
    Skip the local-serve step after building. Useful for scripted verification that only needs
    the artifact on disk.
#>
[CmdletBinding()]
param(
    [switch]$NoServe
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = $PSScriptRoot
$sampleProject = Join-Path $repoRoot 'samples/Formidable.Sample/Formidable.Sample.csproj'
$publishRoot = Join-Path $repoRoot 'artifacts/pages'
$siteDir = Join-Path $publishRoot 'wwwroot'
$basePath = '/formidable/'
$port = 8080

if (Test-Path $publishRoot) {
    Write-Host "Removing previous artifact at '$publishRoot'..."
    Remove-Item -Path $publishRoot -Recurse -Force
}

Write-Host "Publishing the hosted-demo build (HostedDemo=true, Release)..."
dotnet publish $sampleProject -c Release -p:HostedDemo=true -o $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish exited with code $LASTEXITCODE."
}

if (-not (Test-Path $siteDir)) {
    throw "Expected a published site at '$siteDir' but it does not exist."
}

$indexPath = Join-Path $siteDir 'index.html'
if (-not (Test-Path $indexPath)) {
    throw "Expected '$indexPath' but it does not exist."
}

Write-Host "Rewriting <base href> for the GitHub Pages path '$basePath'..."
$indexContent = Get-Content -Path $indexPath -Raw
$rewritten = $indexContent -replace '<base href="/"\s*/>', "<base href=`"$basePath`" />"
if ($rewritten -eq $indexContent) {
    throw "Did not find <base href=`"/`" /> in '$indexPath' - nothing was rewritten. Has the published template changed?"
}
Set-Content -Path $indexPath -Value $rewritten -NoNewline

Write-Host 'Adding the SPA 404 fallback and .nojekyll...'
Copy-Item -Path $indexPath -Destination (Join-Path $siteDir '404.html') -Force
New-Item -Path (Join-Path $siteDir '.nojekyll') -ItemType File -Force | Out-Null

Write-Host "Hosted-demo artifact ready at '$siteDir'."

if ($NoServe) {
    Write-Host 'Skipping the local serve step (-NoServe).'
    return
}

# Local serving replicates the GitHub Pages project-page path (/formidable/) via an NTFS
# junction rather than a copy, so the rewritten <base href> is exercised exactly as a real
# deploy would see it - asset requests resolve under the same path prefix - without duplicating
# the published bits.
$linkPath = Join-Path $publishRoot 'formidable'
if (Test-Path $linkPath) {
    Remove-Item -Path $linkPath -Recurse -Force
}
New-Item -ItemType Junction -Path $linkPath -Target $siteDir | Out-Null

$url = "http://localhost:$port$basePath"

function Test-CommandAvailable {
    param([string]$Name)
    return [bool](Get-Command -Name $Name -ErrorAction SilentlyContinue)
}

Write-Host "Serving '$publishRoot' - browse to $url"
Write-Host 'Press Ctrl+C to stop.'

if (Test-CommandAvailable -Name 'dotnet-serve') {
    dotnet serve --directory $publishRoot --port $port --address 127.0.0.1
}
elseif (Test-CommandAvailable -Name 'python') {
    Push-Location $publishRoot
    try {
        python -m http.server $port --bind 127.0.0.1
    }
    finally {
        Pop-Location
    }
}
elseif (Test-CommandAvailable -Name 'npx') {
    # Pinned to a specific serve release (rather than an unpinned "latest" resolve) and bound to
    # loopback only, matching the python branch above - an unpinned npx fallback would silently
    # execute whatever the current serve release happens to be on the maintainer's own machine.
    npx --yes serve@14.2.6 $publishRoot --listen tcp://127.0.0.1:$port
}
else {
    Write-Warning 'No local static server was found (tried: dotnet serve, python, npx serve).'
    Write-Warning "Install one of them, or serve '$publishRoot' yourself, then browse to $url"
}
