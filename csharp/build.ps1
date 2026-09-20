param([switch]$Publish)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet restore --locked-mode --disable-parallel
    if ($LASTEXITCODE) { throw 'Restore fehlgeschlagen.' }
    dotnet build -c Release -m:1 --no-restore
    if ($LASTEXITCODE) { throw 'Build fehlgeschlagen.' }
    dotnet test -c Release -m:1 --no-build --no-restore
    if ($LASTEXITCODE) { throw 'Tests fehlgeschlagen (FFmpeg/ffprobe auf PATH erforderlich).' }
    if ($Publish) {
        foreach ($project in @('Cli', 'Desktop')) {
            dotnet publish "$project/$project.csproj" -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -m:1 -o "../output/csharp-publish/$project"
            if ($LASTEXITCODE) { throw "Publish fehlgeschlagen: $project" }
        }
    }
} finally { Pop-Location }
