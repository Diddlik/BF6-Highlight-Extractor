# BF6 Highlight Extractor (C#)

Analysiert lange Battlefield-6-Aufnahmen, erkennt eigene Kills und schneidet daraus Clips.
Alles läuft lokal: keine Cloud-OCR, kein Download zur Laufzeit, keine Telemetrie.

Die Anwendung ist die C#-Portierung einer Python-Version. Das Verhalten ist an deren
Referenzdaten geprüft; die Vergleichsdateien liegen unter `Tests/reference/`.

## Was sie tut

Eine Aufnahme wird mit einer festen Rate abgetastet. Nur wenn sich der Killfeed-Ausschnitt
verändert hat, läuft OCR. Erscheint der eigene Spielername auf der **Killer-Seite** einer
Zeile, ist das ein Kill; steht er auf der Opferseite, ist es ein Tod und wird verworfen.
Weil eine Zeile mehrere Sekunden stehen bleibt, werden Wiederholungen zusammengefasst.
Aus den Zeitpunkten entstehen Clip-Abschnitte, die sich vor dem Export prüfen und
korrigieren lassen.

Alternativ erkennt der Vorlagenmodus die persönliche Kill-Bestätigung per Template-Matching.

## Voraussetzungen

- Windows 11 x64
- .NET 10 SDK zum Bauen; das fertige Paket braucht keine installierte Laufzeit
- FFmpeg und ffprobe auf dem PATH zum Bauen und Testen; im Paket liegen sie bei

## Bauen, testen, paketieren

```powershell
.\build.ps1                 # restore, build, test
.\build.ps1 -Publish        # zusätzlich self-contained nach ..\output\csharp-publish
.\package.ps1 -Zip          # Paket mit FFmpeg, Modellen, Lizenzen und Beispielkonfiguration
dotnet test -m:1            # nur die Tests
```

```powershell
dotnet tool install -g vpk          # einmalig
.elease.ps1 -Version 0.2.0        # Installer und Portable-ZIP nach ..\outputelopack
.elease.ps1 -Version 0.2.0 -Publish -Token $env:GITHUB_TOKEN   # als GitHub-Release
```

Die ausgelieferte Anwendung sucht selbst nach neuen Releases dieses Repositorys. Die Suche
beim Start lässt sich abschalten, und **Update prüfen** in den Einstellungen sucht sofort.
Einzelheiten in [docs/CSHARP_DEPLOYMENT.md](../docs/CSHARP_DEPLOYMENT.md).

`package.ps1` kopiert FFmpeg aus einer vorhandenen Installation. Welche Buildoptionen dabei
übernommen werden, entscheidet über die Weitergabebedingungen des Pakets; das steht in
[PACKAGE-NOTICE.md](PACKAGE-NOTICE.md).

## Oberfläche

```powershell
dotnet run --project Desktop
```

Vier Bereiche: Videos hinzufügen, Einstellungen, Analyse, Highlights prüfen und
exportieren. Dazu ein Bereichseditor auf einem frei gewählten Frame, eine Videovorschau
und das Sammeln beschrifteter Prüfdaten. Einzelheiten in
[docs/CSHARP_GUI.md](../docs/CSHARP_GUI.md) des Hauptprojekts.

## Kommandozeile

```powershell
bf6-highlights.exe config-import config.example.yaml config.yaml
bf6-highlights.exe analyze "D:\Aufnahmen\match.mkv" config.yaml "D:\Highlights" --export
```

| Befehl | Zweck |
| --- | --- |
| `analyze VIDEO KONFIG ZIEL [--export]` | Analyse mit Berichten, auf Wunsch mit Clips |
| `export VIDEO EVENTS.json KONFIG ZIEL` | Clips aus einer vorhandenen `events.json` |
| `inspect-frame VIDEO KONFIG ZEIT [ZIEL]` | Erkennung auf einem Einzelbild nachvollziehen |
| `configure-region VIDEO KONFIG [ZEIT] [NAME]` | Killfeed-Bereich mit der Maus festlegen |
| `config-check KONFIG` | Konfiguration prüfen, Fehler je Feld |
| `config-import PYTHON.yaml ZIEL.yaml` | Python-Konfiguration übernehmen |
| `sample VIDEO START ENDE ZEIT LABEL ZIEL` | Prüfdaten sammeln |
| `probe`, `frame`, `clip`, `ocr`, `frame-check` | Einzelschritte zur Diagnose |

Ohne Argumente zeigt die Anwendung diese Liste. Strg+C bricht ab, ohne halbe Dateien zu
hinterlassen; bereits gefundene Ereignisse bleiben erhalten.

## Ausgabe

Je Aufnahme entstehen `events.json`, `events.csv`, `segments.json` und `summary.json` im
Format der Python-Referenz, dazu `clips/` beim Export. Vorhandene Clips werden nie
überschrieben, Quellvideos nie verändert.

## Aufbau

| Projekt | Inhalt |
| --- | --- |
| `Core` | Konfiguration, Frame-Strom, Änderungserkennung, OCR, Erkennung, Deduplizierung, Clips, Berichte |
| `Cli` | Kommandozeile |
| `Desktop` | Avalonia-Oberfläche |
| `Tests` | 203 xUnit-Tests; Videos werden im Test mit FFmpeg erzeugt, OCR wird eingesetzt |

## Stand

Die Fachlogik und die Bedienwege sind umgesetzt und getestet. Offen sind die
Erkennungsabnahme auf einem größeren, beschrifteten Datensatz, die Leistungsmessung gegen
die Python-Version und der Test auf einem frisch aufgesetzten Windows.
