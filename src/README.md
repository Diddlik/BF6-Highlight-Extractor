# BF6 Highlight Extractor · Quelltext

Diese Datei beschreibt das Bauen und den Aufbau der Projekte. Was die Anwendung tut und wie man sie bedient, steht in der
[README des Projekts](../README.md).


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

Build und Restore schreiben gemeinsam nach `artifacts/`: getrennt nach Projekt und
Konfiguration, nicht mehr in lokale `bin`- und `obj`-Ordner der einzelnen Projekte.

```powershell
dotnet tool install -g vpk          # einmalig
.\release.ps1 -Version 0.2.0        # Installer und Portable-ZIP nach ..\output\velopack
.\release.ps1 -Version 0.2.0 -Publish -Token $env:GITHUB_TOKEN   # als GitHub-Release
```

Die ausgelieferte Anwendung sucht selbst nach neuen Releases dieses Repositorys. Die Suche
beim Start lässt sich abschalten, und **Update prüfen** in den Einstellungen sucht sofort.
Einzelheiten in [docs/CSHARP_DEPLOYMENT.md](../docs/CSHARP_DEPLOYMENT.md).

`package.ps1` kopiert FFmpeg aus einer vorhandenen Installation. Welche Buildoptionen dabei
übernommen werden, entscheidet über die Weitergabebedingungen des Pakets; das steht in
[PACKAGE-NOTICE.md](PACKAGE-NOTICE.md).

## Oberfläche

```powershell
dotnet run --project BFHE.UI
```

Vier Bereiche: Videos hinzufügen, Einstellungen, Analyse, Highlights prüfen und
exportieren. Im Bereichseditor springt der Slider direkt durch das Video; das Rechteck
wird auf dem eingebetteten Bild gezeichnet und mit **Übernehmen** gespeichert. Die
Highlight-Vorschau bietet Play/Pause, Seek sowie anpassbare Start- und Endgrenzen.
Beschriftete Prüfdaten lassen sich unabhängig von einem Analyseergebnis sammeln. Einzelheiten in
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

## Bildmarke

`Assets/icon.svg` ist die Vorlage, `Assets/icon-small.svg` die vereinfachte Fassung für
kleine Größen. Daraus entstehen `icon.ico` für die Programmdateien und den Installer,
`icon-256.png` für Fenster und Seitenleiste sowie `logo.png` als Wortmarke:

```powershell
cd Assets
magick -background none icon-small.svg -resize 32x32 icon-32.png   # 16, 24, 32
magick -background none icon.svg -resize 256x256 icon-256.png      # 48, 64, 128, 256
magick icon-16.png icon-24.png icon-32.png icon-48.png icon-64.png icon-128.png icon-256.png icon.ico
magick -background none logo.svg -resize 1420x300 logo.png
```

Gebraucht wird ImageMagick mit librsvg. Die kleinen Größen verzichten auf die Klammern,
weil sie unter 48 Pixeln zu einer grauen Fläche verschmelzen.

| Projekt | Inhalt |
| --- | --- |
| `Core` | Konfiguration, Frame-Strom, Änderungserkennung, OCR, Erkennung, Deduplizierung, Clips, Berichte |
| `Cli` | Kommandozeile |
| `BFHE.UI` | Avalonia-Oberfläche |
| `Tests` | 227 xUnit-Tests einschließlich echter Video-, OCR- und Windows-Fensterprüfungen |

## Stand

Die Fachlogik und die wesentlichen Bedienwege sind umgesetzt und automatisiert geprüft.
Offen bleiben die Erkennungsabnahme auf einem größeren beschrifteten Datensatz, ein
60-Minuten- und Mehrvideo-Lauf, MKV/MOV/AVI-Prüfungen, Tastatur- und Skalierungsabnahme
sowie der Pakettest auf einem frisch aufgesetzten Windows.
