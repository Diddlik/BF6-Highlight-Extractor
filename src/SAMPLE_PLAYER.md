# Sample-Player: Bedienung und Weiterentwicklung

Diese Seite richtet sich an Entwickler, die den manuellen Sample-Workflow ohne den
vorherigen Gesprächskontext weiterbauen. Danach sollen sie den aktuellen Stand prüfen,
die offenen Grenzen verstehen und die nächste Ausbaustufe umsetzen können.

## Ziel

Der Sample-Player verbindet zwei Arbeitsweisen:

- **Keyboard-First:** Im Video navigieren und ein Ereignis mit einer Taste markieren.
- **Review vor Export:** Markierungen zunächst sammeln, prüfen und erst danach als
  unveränderliche Sample-Ordner exportieren.

Ein falscher Tastendruck erzeugt noch keine Dateien und kann mit `Strg+Z` oder über die
Markierungsliste entfernt werden.

## Aktueller Bedienablauf

1. Eine Aufnahme importieren und **Prüfsample …** öffnen.
2. Einmal den gemeinsamen Zielordner wählen.
3. Im Player navigieren und Ereignisse markieren.
4. Markierungen rechts prüfen. Ein Klick springt zur markierten Position und öffnet den
   Bearbeitungsbereich: Label wechseln oder den Zeitpunkt auf die aktuelle Position setzen.
   Auf der Zeitleiste liegt je Markierung ein farbiger Punkt, die ausgewählte ist breiter.
5. Spieler und Datensatz-Aufteilung kontrollieren.
6. **Alle exportieren** erzeugt pro Markierung einen eigenen Sample-Ordner.

| Taste | Aktion |
| --- | --- |
| `Leertaste` | Wiedergabe starten oder pausieren |
| `←` / `→` | eine Sekunde zurück oder vor |
| `Alt+←` / `Alt+→` | fünf Sekunden zurück oder vor |
| `Umschalt+←` / `Umschalt+→` | ein Einzelbild zurück oder vor |
| `K` | eigener Kill |
| `F` | fremder Kill |
| `D` | eigener Tod |
| `H` | Headshot |
| `M` | Markierung ohne Kill |
| `N` | kein Ereignis |
| `X` | mehrere Kills |
| `Strg+Z` | letzte Markierung entfernen |

Während Spielername oder Datensatz-Auswahl den Tastaturfokus besitzen, werden keine
Label-Kürzel ausgelöst.

Der Einzelbildschritt rechnet mit der Bildrate der Quelle. LibVLC springt dabei zum nächsten
Keyframe; das exportierte Standbild schneidet weiterhin FFmpeg exakt am Zeitpunkt.

Offene Markierungen werden nach jeder Änderung als `.bf6-sample-drafts.json` neben dem
Zielordner abgelegt und beim nächsten Öffnen derselben Aufnahme wieder angeboten. Nach einem
vollständig erfolgreichen Export wird die Datei gelöscht; nach einem Teilfehler bleibt sie
liegen.

## Exportregeln

Jede Markierung wird zu einem eigenen Ordner mit `clip.mp4`, `frame.png` und
`sample.json`. Das Zeitfenster reicht standardmäßig drei Sekunden vor bis drei Sekunden
nach der Markierung und wird an den Videogrenzen abgeschnitten.

Die bestehenden internen Label-IDs bleiben unverändert. `label_hint` enthält weiterhin
genau ein Label, und `status` bleibt `needs_review`. Spieler und Split gelten für alle
Markierungen der Sitzung. Vorhandene Ziele werden nicht überschrieben; weitere Samples
erhalten einen numerischen Namenszusatz. Vollständig exportierte Samples bleiben erhalten,
wenn ein späterer Eintrag im Stapel fehlschlägt.

## Technisches Modell

`SampleWindow` besitzt den LibVLC-Player, die Seek-Steuerung, die Zeitleistenmarker und die
sichtbare Markierungsliste. `SampleSession` hält ausschließlich noch nicht exportierte
Punktmarkierungen, sichert sie als Entwurf und erzeugt daraus validierte Exportaufträge. `MainViewModel` führt
diese Aufträge nacheinander aus. `SampleExporter` bleibt die Grenze für atomare
Dateierzeugung und Non-Overwrite.

Wichtige Invarianten:

- Eine Markierung verändert weder Quelle noch bestehende Samples.
- Export beginnt erst nach der ausdrücklichen Schaltfläche.
- Gleiches Label am praktisch gleichen Zeitpunkt wird nicht doppelt aufgenommen.
- Jeder Exportauftrag besitzt ein eindeutiges Ziel.
- Ein Markierungszeitpunkt liegt innerhalb des erzeugten Clipfensters.
- UI-Namen und gespeicherte Label-IDs sind getrennt.

## Umgesetzt seit der ersten Fassung

- **Frame-Schritt** über `Umschalt+←` und `Umschalt+→`, abgeleitet aus der Bildrate der Quelle.
- **Marker auf der Zeitleiste** als farbige Punkte je Label, mit Zeit und Label als Tooltip.
  Die Liste rechts bleibt die zugängliche Textdarstellung.
- **Marker bearbeiten**: Label wechseln und Zeitpunkt auf die aktuelle Position setzen.
  Beides prüft dieselbe Doppelungsregel wie das Anlegen und rührt exportierte Ordner nicht an.
- **Session-Wiederherstellung** über `.bf6-sample-drafts.json` neben dem Zielordner.

Geprüft von `Tests/SampleSessionTests.cs`: Verschieben, Umbenennen, abgelehnte Doppelungen,
Wiederherstellung, Zuordnung zur richtigen Quelle und Löschen nach dem Export.

## Bewusst noch offen

1. **Manueller UI-Smoke-Test:** lange Aufnahme öffnen, mehrfach seeken, alle Kürzel
   prüfen, Einträge anspringen, bearbeiten, entfernen und mindestens zwei Samples gesammelt
   exportieren. Tastaturfokus, Videowiedergabe und die native LibVLC-Darstellung lassen sich
   nicht sinnvoll automatisiert prüfen.
2. **Exportleistung:** Quell-Hash pro Video wiederverwenden und Exporte begrenzt
   parallelisieren, ohne Player oder Datenträger zu überlasten. Heute wird der SHA-256 der
   Quelle für jedes Sample neu berechnet, bei großen Aufnahmen spürbar.
3. **Kombinierte Ereignisse:** erst nach einer Schemaentscheidung umsetzen. Das heutige
   `label_hint` kann beispielsweise `own_kill` und `headshot` nicht gleichzeitig tragen.
4. **Reihenfolge der Liste** bleibt die Reihenfolge des Markierens, auch nach dem Verschieben
   eines Zeitpunkts. Eine Sortierung nach Zeit wäre beim Prüfen angenehmer, ändert aber die
   Nummerierung der Zielordner und ist deshalb eine eigene Entscheidung.

## Nicht stillschweigend ändern

- Ein Headshot ist im aktuellen Format ein eigenes Label, kein zusätzliches Kill-Label.
- Split gilt pro Quellvideo beziehungsweise Sitzung, damit nahe Samples nicht zufällig auf
  Entwicklung und Holdout verteilt werden.
- Exportierte Sample-Ordner sind unveränderlich. Korrekturen werden vor dem Export am
  Entwurf vorgenommen oder später als neues Sample angelegt.
- Hintergrundexport während des Markierens ist absichtlich noch nicht aktiv. Erst eine
  persistierte Queue kann Undo, Absturzschutz und Non-Overwrite zugleich sauber garantieren.

## Prüfen

```powershell
dotnet test src/Tests/Tests.csproj -m:1 --no-restore
.\src\build.ps1 -Publish
```

Die manuelle UI-Abnahme bleibt nötig, weil Tastaturfokus, Videowiedergabe und native
LibVLC-Darstellung durch reine Unit-Tests nicht vollständig abgedeckt sind.
