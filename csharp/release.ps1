# Builds the Windows installer with Velopack and, on request, publishes it as a GitHub release.
#
#   .\csharp\release.ps1 -Version 0.2.0
#   .\csharp\release.ps1 -Version 0.2.0 -Publish -Token $env:GITHUB_TOKEN
#
# The token needs write access to the repository. Without -Publish the release stays local
# in output/velopack and can be installed from there.
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$FfmpegDirectory = '',
    [string]$RepoUrl = 'https://github.com/Diddlik/BF6-Highlight-Extractor',
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$Channel = 'win',
    [switch]$Prerelease,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Version muss wie 1.2.3 oder 1.2.3-beta.1 aussehen: $Version"
}

Push-Location $PSScriptRoot
try {
    # vpk is a local dotnet tool; install it once with: dotnet tool install -g vpk
    $vpk = Get-Command vpk -ErrorAction SilentlyContinue
    if (-not $vpk) {
        $candidate = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
        if (-not (Test-Path -LiteralPath $candidate)) {
            throw 'vpk nicht gefunden. Einmalig installieren: dotnet tool install -g vpk'
        }
        $vpk = $candidate
    }
    else { $vpk = $vpk.Source }

    # The payload is the packaged application: app, runtime, models, FFmpeg and licences.
    & "$PSScriptRoot/package.ps1" -FfmpegDirectory $FfmpegDirectory -Version $Version

    $publishRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../output/csharp-publish'))
    $payload = Join-Path $publishRoot 'Desktop'
    if (-not (Test-Path -LiteralPath (Join-Path $payload 'Desktop.exe'))) {
        throw "Desktop.exe fehlt in $payload"
    }
    $releases = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../output/velopack'))
    New-Item -ItemType Directory -Force -Path $releases | Out-Null

    & $vpk pack `
        --packId BF6HighlightExtractor `
        --packTitle 'BF6 Highlight Extractor' `
        --packAuthors 'BF6 Highlight Extractor' `
        --packVersion $Version `
        --packDir $payload `
        --mainExe Desktop.exe `
        --channel $Channel `
        --outputDir $releases
    if ($LASTEXITCODE) { throw 'vpk pack fehlgeschlagen.' }

    if ($Publish) {
        if (-not $Token) { throw 'Kein Token. -Token angeben oder GITHUB_TOKEN setzen.' }
        $arguments = @(
            'upload', 'github',
            '--outputDir', $releases,
            '--repoUrl', $RepoUrl,
            '--token', $Token,
            '--channel', $Channel,
            '--tag', "v$Version",
            '--releaseName', "BF6 Highlight Extractor $Version",
            '--publish'
        )
        if ($Prerelease) { $arguments += '--pre' }
        & $vpk @arguments
        if ($LASTEXITCODE) { throw 'vpk upload fehlgeschlagen.' }
        Write-Host "Release v$Version veröffentlicht: $RepoUrl/releases"
    }
    else {
        Write-Host "Paket liegt in $releases. Mit -Publish wird daraus ein GitHub-Release."
    }
} finally { Pop-Location }
