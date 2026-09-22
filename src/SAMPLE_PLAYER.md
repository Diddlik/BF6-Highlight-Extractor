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
- **Nächster Kill** über die Schaltfläche oder `Bild↓`, **Nächster Tod** über `Bild↑`:
  Ab der aktuellen Position läuft die konfigurierte Erkennung (inklusive aktivem persönlichem
  Profil) weiter, bis sie das gesuchte Ereignis erkennt; dieser Zeitpunkt wird angesprungen.
  Die Suche läuft in zwei Stufen:
  ein Grobdurchlauf über die Keyframes der Aufnahme, weil FFmpeg dafür nichts dazwischen
  dekodieren muss, danach ein Feindurchlauf mit der konfigurierten Abtastrate zwischen dem
  letzten leeren Grobframe und dem Treffer, der den ersten Frame des Eintrags findet.
  Maßstab für beide ist `deduplication.duplicate_window_seconds`: So lange bleibt ein
  Killfeed-Eintrag stehen, deshalb darf der Grobdurchlauf keinen größeren Abstand haben.
  Liegen die Keyframes weiter auseinander, greift stattdessen ein fester Frameabstand von
  einem halben Fenster. Die Suche nach dem eigenen Tod nimmt denselben Weg und wertet nur die
  verworfenen Zeilen aus; im Vorlagen-Modus steht sie nicht zur Verfügung. Killfeed-Einträge, die
  an der Startposition schon sichtbar sind, gelten nicht als neuer Treffer. Das ist nur eine
  Navigationshilfe; der Nutzer prüft das Bild und vergibt das Label weiterhin selbst.
- **Marker auf der Zeitleiste** als farbige Punkte je Label, mit Zeit und Label als Tooltip.
  Die Liste rechts bleibt die zugängliche Textdarstellung.
- **Marker bearbeiten**: Label wechseln und Zeitpunkt auf die aktuelle Position setzen.
  Beides prüft dieselbe Doppelungsregel wie das Anlegen und rührt exportierte Ordner nicht an.
- **Session-Wiederherstellung** über `.bf6-sample-drafts.json` neben dem Zielordner.
- **Quellidentität pro Exportlauf:** Der SHA-256 einer Aufnahme wird einmal vorbereitet
  und für alle daraus exportierten Samples wiederverwendet.

Geprüft von `Tests/SampleSessionTests.cs`: Verschieben, Umbenennen, abgelehnte Doppelungen,
Wiederherstellung, Zuordnung zur richtigen Quelle und Löschen nach dem Export.

## Persönliches Erkennungsprofil

Der nächste Entwicklungsschritt ist ein vollständig lokales Trainingsverfahren. Nutzer
sollen ihre geprüften Samples auswählen, daraus ein persönliches Erkennungsprofil erzeugen
und dieses Profil für spätere Analysen aktivieren können. Video, Samples, Profil und
Auswertung verlassen den Rechner nicht.

### Umfang der ersten Version

Version 1 trainiert noch kein neuronales Netz und verändert die mitgelieferten OCR-Modelle
nicht. Sie kalibriert die vorhandene Erkennung auf das Material des Nutzers:

- Namensähnlichkeit und OCR-Konfidenz
- notwendige Bestätigungen und Gruppierungsabstand
- Schwellenwerte des Vorlagenmodus, falls dieser verwendet wird
- getrennte Profile für abweichende Auflösungen oder HUD-Anordnungen

Die Kalibrierung durchsucht einen begrenzten Satz zulässiger Parameterkombinationen und
wählt die beste Kombination anhand der bestätigten Entwicklungsdaten. Das Ergebnis ist ein
kleines, versioniertes Profil, kein verändertes globales Modell. Die normale
Standarderkennung bleibt jederzeit verfügbar.

Positive Klassen sind `own_kill`, `headshot` und `multiple_kills`. `own_death`,
`foreign_kill`, `no_event` und `no_event_marker` dienen als wichtige Negativbeispiele.
Eigene Tode findet der Player selbst: Die Erkennung verwirft eine Killfeed-Zeile mit dem
eigenen Namen auf der Opferseite mit dem Grund `name_not_on_killer_side`, und genau diese
Zeilen sucht **Nächster Tod**. Fremde Kills und Markierungen bleiben Handarbeit, für sie
gibt es kein Erkennungssignal.
Langfristig zählt `expected_events` als Wahrheit. Ein bloßes `label_hint` darf nur nach
einer ausdrücklichen Bestätigung im Trainingsdialog verwendet werden.

### Nutzerablauf

1. **Persönliches Profil trainieren** öffnen und einen lokalen Sample-Sammelordner wählen.
2. Die Anwendung prüft Schema, Dateien, Labels, Quellzuordnung und Datensatz-Split.
3. Ungültige oder ungeprüfte Fälle werden mit einem konkreten Grund angezeigt und nicht
   stillschweigend verwendet.
4. Die Anwendung kalibriert ausschließlich mit `development`-Fällen.
5. `holdout`-Fälle werden erst danach einmalig zur unabhängigen Bewertung verwendet.
6. Der Ergebnisdialog zeigt Fallzahlen, Fehlklassifikationen und Qualitätswerte im Vergleich
   zur Standardkonfiguration.
7. Der Nutzer kann das neue Profil aktivieren, verwerfen oder später wieder deaktivieren.

Die Analyse zeigt sichtbar, ob die Standarderkennung oder ein persönliches Profil aktiv
ist. Fehlt das Profil, ist es beschädigt oder passt es nicht zur Videoauflösung, wird mit
einer verständlichen Meldung auf die Standarderkennung zurückgefallen.

### Profilinhalt und Grenzen

Ein Profil enthält mindestens:

- eindeutige Profil-ID, Anzeigename, Erstellungszeit und Formatversion
- Version der Anwendung und der zugrunde liegenden OCR-Modelle
- passende Auflösung und verwendeten Erkennungsbereich
- kalibrierte Parameter und Erkennungsmodus
- Prüfsummen der verwendeten Sample-Manifeste
- Anzahl der Entwicklungs- und Holdout-Fälle je Label
- gemessene Precision, Recall und F1 für Standard- und persönliche Konfiguration

Profile liegen im lokalen Benutzerprofil und werden atomar geschrieben. Ein neuer Lauf
überschreibt kein bestehendes Profil, sondern erzeugt eine neue Version. Quelldateien und
Sample-Ordner bleiben unverändert.

Samples derselben Quellaufnahme dürfen nicht zwischen Entwicklung und Holdout verteilt
werden. Andernfalls würden fast identische Bilder die Bewertung künstlich verbessern.
Ebenso dürfen Vorhersagen des gerade trainierten Profils niemals automatisch zu neuen
Trainingslabels werden.

### Stand der Umsetzung

Umgesetzt in `Core/PersonalProfile.cs`, `Core/PersonalCalibration.cs` und
`Core/PersonalProfileStore.cs`, geprüft von `Tests/PersonalProfileTests.cs`:

- Einlesen der Samples mit Schema-, Datei-, Label-, Quell- und Splitprüfung; jeder
  übersprungene Fall nennt seinen Grund.
- Ablehnung, wenn Entwicklung und Holdout dieselbe Quellaufnahme teilen.
- Kalibrierung von **Mindestkonfidenz und Namensähnlichkeit** ausschließlich auf
  Entwicklungsfällen. Die Kandidatenwerte für die Konfidenz stammen aus dem Material selbst,
  die Namensschwellen aus einer festen Leiter. Gewählt wird nach F1, dann Präzision, dann
  der strengeren Einstellung — deterministisch und wiederholbar.
- Messung auf dem Holdout erst nach der Wahl, zusätzlich derselbe Vergleich mit der
  Standardkonfiguration.
- Profile im Benutzerordner, atomar geschrieben, versioniert und nie überschrieben.
- Aktivieren und Deaktivieren über eine eigene Datei neben den Profilen; die Konfiguration
  des Nutzers bleibt unangetastet.
- Rückfall auf die Standarderkennung bei fehlendem, unlesbarem oder unpassendem Profil,
  jeweils mit Begründung. Die Analyse nennt sichtbar, welche Erkennung gilt.

Bedienung über die Oberfläche unter *Einstellungen · Persönliches Erkennungsprofil* oder
über die Kommandozeile:

```powershell
bf6-highlights.exe train-profile SAMPLE-ORDNER config.yaml [--allow-hints] [--activate]
bf6-highlights.exe profiles
bf6-highlights.exe profile-activate PROFIL.json
bf6-highlights.exe profile-off
```

Auf den zwölf vorhandenen Referenzfällen hebt die Kalibrierung die Trefferquote von 78 % auf
89 % bei unveränderter Präzision von 100 %, also F1 von 0,88 auf 0,94. Das ist ein Wert auf
denselben Fällen, mit denen kalibriert wurde, und ohne Holdout — er belegt, dass der Ablauf
funktioniert, nicht dass die Erkennung allgemein besser wird.

### Noch nicht kalibriert

- Notwendige Bestätigungen und Gruppierungsabstand des Vorlagenmodus.
- Schwellenwerte des Vorlagenmodus selbst.
- Zeilen- und Gegnerähnlichkeit der Deduplizierung.

Diese Werte bleiben so, wie sie in der Konfiguration stehen. Sie kommen erst dazu, wenn
genug beschriftetes Material vorliegt, um ihre Wirkung überhaupt zu messen.

### Abnahmekriterien

- Ein Profil lässt sich ohne Netzwerkzugriff aus gültigen lokalen Samples erstellen.
- Der Trainingslauf lehnt einen Satz ohne positive oder ohne negative Beispiele ab.
- Ungeprüfte Fälle werden nicht unbemerkt als Wahrheit behandelt.
- Kein Holdout-Fall beeinflusst die gewählten Parameter.
- Vor Aktivierung wird der direkte Vergleich zur Standarderkennung angezeigt.
- Aktivieren und Deaktivieren ändert keine globale Konfiguration und keine Samples.
- Ein fehlendes, inkompatibles oder beschädigtes Profil verhindert keine Analyse mit der
  Standarderkennung.
- Profil und Ergebnisbericht werden reproduzierbar und versioniert gespeichert.
- Automatisierte Tests decken Split-Leakage, Profilvalidierung, deterministische Auswahl,
  Fallback und Non-Overwrite ab.

### Später, nicht Teil von Version 1

Ein kleiner Bildklassifikator kann später lokal trainiert und als ONNX-Modell geladen
werden. Er soll relevante Killfeed-Änderungen vorsortieren; OCR und bestehende Regeln
bestimmen weiterhin Spielername und Richtung. Erst reale Messungen mit ausreichend vielen
Nutzersamples sollen entscheiden, ob dieser zusätzliche Modelltyp besser als die lokale
Kalibrierung ist. OCR-Finetuning, Cloud-Training und automatisches Selbstlabeln gehören
ausdrücklich nicht zum nächsten Ziel.

## Bewusst noch offen

1. **Manueller UI-Smoke-Test:** lange Aufnahme öffnen, mehrfach seeken, alle Kürzel
   prüfen, Einträge anspringen, bearbeiten, entfernen und mindestens zwei Samples gesammelt
   exportieren. Tastaturfokus, Videowiedergabe und die native LibVLC-Darstellung lassen sich
   nicht sinnvoll automatisiert prüfen.
2. **Exportparallelität:** Exporte laufen weiterhin seriell, damit Player und Datenträger
   nicht unkontrolliert belastet werden. Eine begrenzte Parallelisierung braucht zuerst
   Messwerte auf langen Aufnahmen.
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
