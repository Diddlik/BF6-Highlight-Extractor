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
            var analysisService = new AnalysisService(settings, () => new OnnxOcrEngine())
            {
                ProfilePath = PersonalProfileStore.ActivePath,
            };
            var analysis = await analysisService.RunAsync(
                input, destination, new ConsoleProgress(), cancel.Token, withClips);
            Console.WriteLine("Erkennung: " + analysisService.DetectionNote);
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
        case ["inspect-frame", var input, var configuration, var time, .. var target]
            when target.Length <= 1:
            var inspectConfig = ConfigurationFile.Load(configuration);
            var inspection = await FrameInspector.InspectAsync(inspectConfig, input, Seconds(time),
                target.Length == 1 ? target[0] : inspectConfig.Video.OutputDirectory,
                () => new OnnxOcrEngine(), cancel.Token);
            var box = inspection.Region;
            Console.WriteLine($"Bereich {box.X},{box.Y} {box.Width}x{box.Height} | Frame "
                + $"{inspection.FrameNumber} | {Reports.FormatTimestamp(inspection.TimestampSeconds)}");
            if (inspectConfig.Detection.Mode == "template")
                Console.WriteLine(inspection.Template is { } match
                    ? $"Treffer: {match.Label} {match.Confidence:0.000} bei "
                      + $"{match.BoundingBox.X},{match.BoundingBox.Y}"
                    : "Keine Vorlage getroffen.");
            else
            {
                foreach (var line in inspection.Lines)
                    Console.WriteLine($"  Text {line.Confidence:0.00} x={line.BoundingBox.X} "
                        + $"y={line.BoundingBox.Y}: {line.Text}");
                foreach (var rejection in inspection.Rejections)
                    Console.WriteLine($"  Verworfen ({rejection.Reason}, {rejection.Score:0.0}): "
                        + rejection.RowText);
                foreach (var candidate in inspection.Candidates)
                    Console.WriteLine($"  Kandidat {candidate.SimilarityScore:0.0}: {candidate.RawText} "
                        + $"-> Gegner {candidate.OpponentName ?? "?"}");
                if (inspection.Lines.Count == 0) Console.WriteLine("  Kein Text erkannt.");
            }
            Console.WriteLine("Ausschnitt: " + inspection.CropPath);
            break;
        case ["next-kill" or "next-death", var input, var configuration, var time]:
            var nextConfig = ConfigurationFile.Load(configuration);
            var nextVideo = await service.ProbeAsync(input, cancel.Token);
            var next = await PotentialFrameFinder.FindNextAsync(nextConfig, nextVideo, Seconds(time),
                () => new OnnxOcrEngine(), PersonalProfileStore.ActivePath,
                new Progress<double>(scanned => Console.Error.Write(
                    Reports.FormatTimestamp(scanned) + "\r")), args[0] == "next-death",
                cancel.Token);
            Console.Error.WriteLine();
            Console.WriteLine(next is { } hit ? Reports.FormatTimestamp(hit)
                : args[0] == "next-death" ? "Kein weiterer Tod gefunden."
                : "Kein weiterer Kill gefunden.");
            break;
        case ["configure-region", var input, var configuration, .. var picker]
            when picker.Length <= 2:
            // Fail on a broken configuration before opening the picker window.
            ConfigurationFile.Load(configuration);
            var probed = await service.ProbeAsync(input, cancel.Token);
            var at = picker.Length >= 1 ? Seconds(picker[0]) : 60.0;
            using (var frame = await FrameInspector.CropAsync(probed,
                Math.Min(at, Math.Max(0, probed.DurationSeconds - 1)),
                new PixelRegion(0, 0, probed.Width, probed.Height), cancel.Token))
            {
                Console.WriteLine("Rechteck über den Killfeed ziehen, dann ENTER. ESC bricht ab.");
                var picked = OpenCvSharp.Cv2.SelectROI("Killfeed-Bereich wählen", frame);
                OpenCvSharp.Cv2.DestroyAllWindows();
                if (picked.Width <= 0 || picked.Height <= 0)
                {
                    Console.Error.WriteLine("Abgebrochen, kein Bereich gewählt.");
                    return 130;
                }
                var profile = picker.Length == 2 ? picker[1]
                    : $"battlefield6_{probed.Width}x{probed.Height}";
                ConfigurationFile.SaveRegionProfile(configuration, profile,
                    new ResolutionSettings { Width = probed.Width, Height = probed.Height },
                    new RegionSettings
                    {
                        X = picked.X, Y = picked.Y, Width = picked.Width, Height = picked.Height,
                    });
                Console.WriteLine($"Profil '{profile}' in {Path.GetFullPath(configuration)}: "
                    + $"{picked.X},{picked.Y} {picked.Width}x{picked.Height}");
            }
            break;
        case ["train-profile", var samples, var configuration, .. var options]
            when options.All(option => option is "--allow-hints" or "--activate"):
            var trainingConfig = ConfigurationFile.Load(configuration);
            var dataset = PersonalProfileTrainer.LoadDataset(samples,
                options.Contains("--allow-hints"));
            foreach (var issue in dataset.Issues) Console.Error.WriteLine("Übersprungen: " + issue);
            Console.WriteLine($"{dataset.Development.Count} Entwicklungsfälle, "
                + $"{dataset.Holdout.Count} Holdout-Fälle.");
            var trainingCases = await PersonalProfileTrainer.BuildCasesAsync(trainingConfig,
                dataset.Development.Concat(dataset.Holdout), () => new OnnxOcrEngine(),
                new Progress<string>(id => Console.Error.WriteLine("OCR: " + id)), cancel.Token);
            var trained = PersonalProfileTrainer.Calibrate(trainingConfig, trainingCases);
            var profilePath = PersonalProfileStore.Save(trained);
            Console.WriteLine($"Mindestkonfidenz {trained.MinimumConfidence:0.###}, "
                + $"Namensähnlichkeit {trained.NameThreshold:0.#}");
            Console.WriteLine("Entwicklung · persönlich: " + trained.DevelopmentMetrics.Summary);
            Console.WriteLine("Entwicklung · Standard:   " + trained.BaselineDevelopmentMetrics.Summary);
            Console.WriteLine("Holdout · persönlich:     " + trained.HoldoutMetrics.Summary);
            Console.WriteLine("Holdout · Standard:       " + trained.BaselineHoldoutMetrics.Summary);
            Console.WriteLine(profilePath);
            if (options.Contains("--activate"))
            {
                PersonalProfileStore.Activate(profilePath);
                Console.WriteLine("Profil aktiviert.");
            }
            break;
        case ["profiles"]:
            var active = PersonalProfileStore.ActivePath;
            foreach (var (path, entry) in PersonalProfileStore.List())
                Console.WriteLine($"{(path == active ? "*" : " ")} {entry.Name} · "
                    + $"{entry.SourceWidth}×{entry.SourceHeight} · {entry.CreatedAt:yyyy-MM-dd HH:mm} · "
                    + $"F1 {entry.DevelopmentMetrics.F1:0.00} · {path}");
            if (active is null) Console.WriteLine("Aktiv: Standarderkennung");
            break;
        case ["profile-activate", var path]:
            PersonalProfileStore.Activate(path);
            Console.WriteLine("Profil aktiviert: " + Path.GetFullPath(path));
            break;
        case ["profile-off"]:
            PersonalProfileStore.Activate(null);
            Console.WriteLine("Standarderkennung aktiv.");
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
                inspect-frame VIDEO KONFIG.yaml ZEIT [ZIELORDNER]
                next-kill VIDEO KONFIG.yaml ZEIT
                next-death VIDEO KONFIG.yaml ZEIT
                configure-region VIDEO KONFIG.yaml [ZEIT] [PROFILNAME]
                train-profile SAMPLE-ORDNER KONFIG.yaml [--allow-hints] [--activate]
                profiles | profile-activate PROFIL.json | profile-off
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

/// <summary>
/// Progress arrives from several worker threads at once. Writing from all of them interleaves
/// the output and blocks on a redirected stream, so the reports are serialised here and only a
/// changed percentage is printed.
/// </summary>
file sealed class ConsoleProgress : IProgress<AnalysisProgress>
{
    private readonly object gate = new();
    private int reported = -1;

    public void Report(AnalysisProgress update)
    {
        lock (gate)
        {
            var percent = (int)update.Percent;
            if (percent == reported) return;
            reported = percent;
            Console.Error.WriteLine($"{update.Stage} {percent,3} % | Frames {update.FramesSampled}"
                + $" | OCR {update.OcrCalls} | Kills {update.KillsDetected}");
        }
    }
}
