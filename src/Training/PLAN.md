# Plan: Bildprüfung für alle Spieler und weitere Spielsprachen

Stand 0.5.0. Das Zeilenmodell verwirft Pings für jeden Spielernamen, eigene Tode und Sonstiges
nur für `BulletWaltz`. Dieser Plan beschreibt, wie beides für alle Spieler und für weitere
Spielsprachen gilt.

## Ausgangslage (gemessen)

| Zeilen | Ergebnis |
| --- | --- |
| eigene Zeilen, zurückgehaltene Aufnahmen | 98 und 222 geprüfte Zeilen, kein echter Kill verworfen |
| fremde Namen, Pings | 181 von 185 erkannt, kein Kill mit Gegner für einen Ping gehalten |
| fremde Namen, Kill oder Tod | unbrauchbar: 53 Kills für Tode gehalten |

Ursache der letzten Zeile: Das Modell sieht nur das Bild und weiß nicht, welcher Name der
eigene ist. Gelernt hat es „`BulletWaltz` rechts heißt Tod“. Mehr Daten allein beheben das
nicht; das Modell braucht die Angabe, wessen Zeile es beurteilt.

## Teil A: alle Spieler

### A1. Eigenen Namen im Bild markieren (Kern)

Vor der Bewertung wird die Box des eigenen Namens in der Zeile durch eine feste neutrale
Fläche ersetzt, im Training wie in der App. Die Box liefert die Texterkennung schon heute.
Das Modell lernt dann „Markierung links, Waffe, Name rechts heißt Kill“ statt eines
bestimmten Schriftbilds.

- `export-rows` schreibt zusätzlich die Box des erkannten Namens (`name_x0`, `name_x1`).
- `KillCandidate` und `DetectionRejection` tragen die Box; `RowClassifier` markiert sie.
- `train.py` markiert gleich; `RowClassifierTests` sichert die gleiche Aufbereitung.
- Aufwand: klein bis mittel. Risiko: eine ungenaue Box verdeckt einen Teil der Waffe;
  deshalb die Box nur um die Namensbuchstaben, nicht darüber hinaus.

### A2. Trainingsdaten für beliebige Namen aus den vorhandenen Aufnahmen

In den eigenen Aufnahmen stehen hunderte fremde Namen als Killer, Opfer und Pinger. Mit A1
wird jede Zeile zu einem Beispiel aus Sicht des markierten Namens:

- Name links mit Waffe: Kill dieses Namens; Name rechts: Tod dieses Namens; Satz: Ping.
- `export-rows` bekommt eine Option, jeden lesbaren Namen einer Zeile als „eigenen“ zu
  exportieren, nicht nur die eingestellten.
- Ergebnis: zehntausende Zeilen mit vielen Namen, Längen und Farben, ohne neue Aufnahmen.
- Die Vorbeschriftung stimmt laut Prüfung zu etwa 96 %; geprüft wird wie bisher eine
  Stichprobe in der Galerie, vor allem die unsicheren Fälle.

### A3. Messen, bevor ausgeliefert wird

- Zurückhalten nach Aufnahme **und** nach Name: Namen aus dem Prüfteil kommen im Training
  nicht vor.
- Freigabekriterium: auf zurückgehaltenen Namen kein verworfener echter Kill bei der
  Schwelle 0,9, sonst bleibt das Tod-Veto auf trainierte Namen beschränkt.
- Erst wenn das erfüllt ist, entfällt die Namensliste in `row-classifier.json`.

### A4. Auflösung und HUD

Bisher nur 2560×1440 mit Standard-HUD.

- Beim Training Zeilen zufällig skalieren und leicht verschieben, damit andere Auflösungen
  und HUD-Größen nicht fremd wirken.
- Echte Prüfung braucht je eine Aufnahme in 1920×1080, 3840×2160 und Ultrawide sowie eine
  mit geänderter HUD-Größe. Offen: Wer liefert diese Aufnahmen?

### A5. Daten anderer Nutzer (optional, später)

Die Anwendung lädt nichts hoch und soll das auch nicht tun. Wer beitragen möchte:

- exportiert seine Zeilen und die geprüfte `labels.json` lokal und schickt sie bewusst
  selbst, zum Beispiel als Zip an das Repository;
- oder trainiert lokal nach `src/Training` nach und nutzt sein eigenes Modell.

Ein Training in der Anwendung selbst ist nicht geplant: es bräuchte PyTorch im Paket.

## Teil B: weitere Spielsprachen

### B1. Pings in anderen Sprachen

Die Textregel kennt nur deutsche und einzelne englische Wörter (`gepingt`, `hat … ein`,
`abgebrochen`, `pinged`). In einer anderen Spielsprache greift sie nicht.

- Das Bildmodell erkennt Pings am Aufbau (Name, Symbol, Satz), nicht an Wörtern. Es dürfte
  daher auch andere Sprachen erkennen; belegt ist das nicht.
- Prüfung: je eine kurze Aufnahme mit englischer, französischer und spanischer
  Spielsprache, darin einige Pings setzen. Pro Sprache genügen wenige Minuten.
- Die Textregel bekommt die Ping-Wörter dieser Sprachen, sobald die echten Texte bekannt
  sind. Keine geratenen Übersetzungen.

### B2. Texterkennung für andere Schriften

Das OCR-Modell liest lateinische Schrift. Namen in anderen Schriften (etwa
`有纫會`) werden nicht gelesen; der Kill bleibt erhalten, nur ohne Gegnernamen.

- PP-OCRv5 hat Modelle für weitere Schriften. Eine Umstellung betrifft Paketgröße,
  Geschwindigkeit und die geprüften Prüfsummen der Modelle.
- Erst angehen, wenn fehlende Gegnernamen tatsächlich stören.

### B3. Sprache der Oberfläche

Die Anwendung ist deutsch. Eine englische Oberfläche ist ein eigenes, vom Modell
unabhängiges Vorhaben.

## Reihenfolge

| Schritt | Inhalt | Voraussetzung |
| --- | --- | --- |
| 1 | A1 Name markieren | keine |
| 2 | A2 Mehrnamen-Export, Galerie-Stichprobe prüfen (ca. 500 Zeilen) | 1 |
| 3 | A3 Messung nach Aufnahme und Name, Freigabe | 2 |
| 4 | B1 Pings anderer Sprachen prüfen | Aufnahmen in anderen Spielsprachen |
| 5 | A4 andere Auflösungen und HUD-Größen | passende Aufnahmen |
| 6 | A5, B2, B3 | bei Bedarf |

## Offene Entscheidungen

- Wer liefert Aufnahmen in anderen Auflösungen, HUD-Größen und Spielsprachen?
- Sollen Beiträge anderer Nutzer ins Repository, und in welcher Form?
- Englische Oberfläche: ja oder nein?
