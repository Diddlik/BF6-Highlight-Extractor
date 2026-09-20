using System.Globalization;
using System.Text.Json;
using Bf6Highlights;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
var service = new VideoService();
try
{
    switch (args)
    {
        case ["detect-samples", var samples, var player, var x, var y, var width, var height, var report]:
            var interrupted = await ReferenceDetection.RunAsync(samples,
                new DetectionSettings { PlayerNames = [player] },
                new PixelRegion(Pixels(x), Pixels(y), Pixels(width), Pixels(height)), report, cancel.Token);
            Console.WriteLine(Path.GetFullPath(report));
            if (interrupted) return 130;
            break;
        case ["frame-check", var source, var time]:
            Console.WriteLine(JsonSerializer.Serialize(await CodecDiagnostics.CompareAsync(source,
                Seconds(time), cancel.Token), new JsonSerializerOptions { WriteIndented = true }));
            break;
        case ["ocr", var image]:
            using (var engine = new OnnxOcrEngine())
                Console.WriteLine(JsonSerializer.Serialize(await engine.ReadAsync(image, token: cancel.Token),
                    new JsonSerializerOptions { WriteIndented = true }));
            break;
        case ["ocr", var image, var x, var y, var width, var height]:
            using (var engine = new OnnxOcrEngine())
                Console.WriteLine(JsonSerializer.Serialize(await engine.ReadAsync(image,
                    new PixelRegion(Pixels(x), Pixels(y), Pixels(width), Pixels(height)), cancel.Token),
                    new JsonSerializerOptions { WriteIndented = true }));
            break;
        case ["config-check", var configuration]:
            ConfigurationFile.Load(configuration);
            Console.WriteLine("Konfiguration ist gültig: " + Path.GetFullPath(configuration));
            break;
        case ["config-import", var source, var target]:
            var imported = ConfigurationFile.Import(source);
            if (Path.GetFullPath(source) == Path.GetFullPath(target))
                throw new IOException("Quell- und Zieldatei dürfen nicht identisch sein.");
            ConfigurationFile.Save(target, imported.Configuration);
            foreach (var notice in imported.Notices) Console.WriteLine("Hinweis: " + notice);
            Console.WriteLine(Path.GetFullPath(target));
            break;
        case ["analyze", var input, var configuration, var destination, .. var options]
            when options.Length == 0 || options is ["--export"]:
            var settings = ConfigurationFile.Load(configuration);
            var withClips = options.Length == 1;
            var reported = -1;
            var analysis = await new AnalysisService(settings, () => new OnnxOcrEngine()).RunAsync(
                input, destination, new Progress<AnalysisProgress>(update =>
                {
                    if ((int)update.Percent == reported) return;
                    reported = (int)update.Percent;
                    Console.Error.Write($"\r{update.Stage} {reported,3} % | "
                        + $"Frames {update.FramesSampled} | OCR {update.OcrCalls} | "
                        + $"Kills {update.KillsDetected}   ");
                }), cancel.Token, withClips);
            Console.Error.WriteLine();
            Console.WriteLine($"{analysis.Events.Count} Kill-Kandidaten, "
                + $"{analysis.Segments.Count} Clip-Abschnitte, Berichte in {Path.GetFullPath(destination)}");
            Console.WriteLine(withClips
                ? $"{analysis.Clips.Count} Clips in {Path.GetFullPath(Path.Combine(destination, "clips"))}"
                : "Ohne --export werden keine Clips geschrieben.");
            if (analysis.Interrupted) return 130;
            break;
        case ["export", var input, var eventFile, var configuration, var destination]:
            var clipSettings = ConfigurationFile.Load(configuration);
            var probe = await service.ProbeAsync(input, cancel.Token);
            var clipSegments = SegmentBuilder.Build(Reports.ReadEventsJson(eventFile),
                clipSettings.Clips, probe.DurationSeconds);
            Reports.WriteSegmentsJson(Path.Combine(destination, "segments.json"), clipSegments);
            var exported = await new ClipExporter(clipSettings.Clips).ExportAsync(probe.Path,
                clipSegments, Path.Combine(destination, "clips"),
                new Progress<string>(clip => Console.WriteLine("Clip: " + clip)), cancel.Token);
            foreach (var failure in exported.Failures) Console.Error.WriteLine("Fehler: " + failure);
            Console.WriteLine($"{exported.Written.Count} von {clipSegments.Count} Clips in "
                + Path.GetFullPath(Path.Combine(destination, "clips")));
            break;
        case ["version"]:
            Console.WriteLine("bf6-highlights C# 0.1.0-preview (Video/OCR-Prototyp)");
            break;
        case ["probe", var source]:
            Console.WriteLine(JsonSerializer.Serialize(await service.ProbeAsync(source, cancel.Token),
                new JsonSerializerOptions { WriteIndented = true }));
            break;
        case ["frame", var source, var time, var destination]:
            await service.FrameAsync(source, Seconds(time), destination, cancel.Token);
            Console.WriteLine(Path.GetFullPath(destination));
            break;
        case ["clip", var source, var start, var end, var destination]:
            await service.ClipAsync(source, Seconds(start), Seconds(end), destination, cancel.Token);
            Console.WriteLine(Path.GetFullPath(destination));
            break;
        case ["sample", var source, var start, var end, var time, var label, var destination]:
            await SampleExporter.ExportAsync(source, Seconds(start), Seconds(end), Seconds(time),
                label, destination, cancel.Token);
            Console.WriteLine(Path.GetFullPath(destination));
            break;
        default:
            Console.WriteLine("""
                BF6 C#-Video/OCR-Prototyp — Zeiten in Sekunden mit Dezimalpunkt.
                version
                probe VIDEO
                frame VIDEO ZEIT FRAME.png
                clip VIDEO START ENDE CLIP.mp4
                sample VIDEO START ENDE ZEIT LABEL ZIELORDNER
                analyze VIDEO KONFIG.yaml ZIELORDNER [--export]
                export VIDEO EVENTS.json KONFIG.yaml ZIELORDNER
                config-check KONFIG.yaml
                config-import PYTHON-KONFIG.yaml ZIEL-KONFIG.yaml
                frame-check VIDEO ZEIT
                ocr BILD [X Y BREITE HÖHE]
                detect-samples ORDNER SPIELER X Y BREITE HÖHE BERICHT.json
                Liefert vorläufige Kill-Kandidaten je Sample, keine bestätigten Kills.
                OCR: lokale PP-OCRv5-Modelle, CPU, Boxen in Originalbild-Koordinaten.

                Trainings-/Prüfdaten sammeln:
                sample "Aufnahme.mp4" 17 23 20 own_kill "training-data/fall-001"
                Labels: own_kill, own_death, headshot, foreign_kill, no_event,
                        no_event_marker, multiple_kills
                Ergebnis: clip.mp4 + frame.png + sample.json. Keine OCR nötig.
                Labels sind Hinweise: vor Training/Abnahme manuell prüfen.
                Bestehende Ziele werden nie überschrieben. Abbruch: Strg+C.
                Anleitung: docs/TRAINING_DATA.md. Noch keine Python-CLI-Parität.
                """);
            return args is [] or ["--help"] or ["help"] ? 0 : 2;
    }
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Abgebrochen."); return 130; }
catch (ConfigurationException e) { Console.Error.WriteLine(e.Message); return 1; }
catch (Exception e) when (e is IOException or ArgumentException or TimeoutException
                         or System.ComponentModel.Win32Exception or JsonException
                         or OpenCvSharp.OpenCVException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static double Seconds(string value) =>
    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
        && double.IsFinite(result) ? result : throw new ArgumentException("Ungültige Sekunden: " + value);

static int Pixels(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
    out var result) ? result : throw new ArgumentException("Ungültiger Pixelwert: " + value);
