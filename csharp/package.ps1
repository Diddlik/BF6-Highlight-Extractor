# Builds the self-contained Windows package: application, FFmpeg, OCR models and licenses.
# FFmpeg is not part of this repository; it is copied from an existing installation.
#
#   .\csharp\package.ps1
#   .\csharp\package.ps1 -FfmpegDirectory "C:\ffmpeg\bin" -Zip
param(
    [string]$FfmpegDirectory = '',
    [string]$Version = '',
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & "$PSScriptRoot/build.ps1" -Publish -Version $Version

    $publishRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../output/csharp-publish'))

    # FFmpeg and ffprobe: parameter first, otherwise whatever is on PATH.
    if (-not $FfmpegDirectory) {
        $found = Get-Command ffmpeg -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if (-not $found) {
            throw 'Weder -FfmpegDirectory angegeben noch ffmpeg auf PATH gefunden.'
        }
        $FfmpegDirectory = Split-Path -Parent $found.Source
    }
    $tools = @('ffmpeg.exe', 'ffprobe.exe') | ForEach-Object {
        $tool = Join-Path $FfmpegDirectory $_
        if (-not (Test-Path -LiteralPath $tool)) { throw "Nicht gefunden: $tool" }
        $tool
    }

    foreach ($project in @('Cli', 'Desktop')) {
        $target = Join-Path $publishRoot $project
        $toolDirectory = Join-Path $target 'tools'
        New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
        Copy-Item -LiteralPath $tools -Destination $toolDirectory -Force
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../config.example.yaml') `
            -Destination (Join-Path $target 'config.example.yaml') -Force

        # Record which FFmpeg build was shipped; the options decide the licence of the package.
        $version = & (Join-Path $toolDirectory 'ffmpeg.exe') -hide_banner -version
        $licenseDirectory = Join-Path $target 'licenses'
        New-Item -ItemType Directory -Force -Path $licenseDirectory | Out-Null
        Set-Content -LiteralPath (Join-Path $licenseDirectory 'FFMPEG-BUILD.txt') `
            -Value $version -Encoding utf8
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PACKAGE-NOTICE.md') `
            -Destination (Join-Path $target 'PACKAGE-NOTICE.md') -Force
    }

    if ($Zip) {
        foreach ($project in @('Cli', 'Desktop')) {
            $target = Join-Path $publishRoot $project
            $archive = Join-Path $publishRoot "bf6-highlights-$($project.ToLowerInvariant())-win-x64.zip"
            if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
            Compress-Archive -Path (Join-Path $target '*') -DestinationPath $archive
            Write-Host "Paket: $archive"
        }
    }
    Write-Host "Fertig: $publishRoot"
} finally { Pop-Location }
