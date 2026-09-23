param([switch]$Publish, [string]$Version = '')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet restore --locked-mode --disable-parallel
    if ($LASTEXITCODE) { throw 'Restore fehlgeschlagen.' }
    $versionArgument = if ($Version) { "-p:Version=$Version" } else { '' }
    dotnet build -c Release -m:1 --no-restore $versionArgument
    if ($LASTEXITCODE) { throw 'Build fehlgeschlagen.' }
    # CI has no local recordings; GitHub Actions sets CI=true.
    $filterArgument = if ($env:CI -eq 'true') { '--filter', 'Category!=LocalSamples' } else { @() }
    dotnet test -c Release -m:1 --no-build --no-restore @filterArgument
    if ($LASTEXITCODE) { throw 'Tests fehlgeschlagen (FFmpeg/ffprobe auf PATH erforderlich).' }
    if ($Publish) {
        $publishRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../output/csharp-publish'))
        foreach ($project in @('Cli', 'BFHE.UI')) {
            $publishDirectory = [IO.Path]::GetFullPath((Join-Path $publishRoot $project))
            if (-not $publishDirectory.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Ungültiges Publish-Ziel: $publishDirectory"
            }
            if (Test-Path -LiteralPath $publishDirectory) {
                Remove-Item -LiteralPath $publishDirectory -Recurse -Force
            }
            dotnet publish "$project/$project.csproj" -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -m:1 $versionArgument -o $publishDirectory
            if ($LASTEXITCODE) { throw "Publish fehlgeschlagen: $project" }
        }
    }
} finally { Pop-Location }
