using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Bf6Highlights;

/// <summary>One sampled frame; the caller owns and disposes <see cref="Image"/>.</summary>
public sealed record SampledFrame(int Number, double TimestampSeconds, Mat Image) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

/// <summary>
/// Streams every step-th frame of a recording, already cropped, through an FFmpeg pipe. FFmpeg is
/// the frame supplier (see docs/CSHARP_OCR_PROTOTYPE.md), the pipe provides the backpressure and
/// only one frame is held in memory at a time.
/// </summary>
public static class FrameStream
{
    public static IAsyncEnumerable<SampledFrame> ReadAsync(VideoMetadata video, PixelRegion region,
        int samplesPerSecond, CancellationToken token = default, double startSeconds = 0) =>
        ReadEveryAsync(video, region, ChangeDetector.SampleStep(video.Fps, samplesPerSecond), token,
            startSeconds);

    /// <summary>
    /// Streams every <paramref name="step"/>-th frame, optionally only up to
    /// <paramref name="stopSeconds"/>. A coarse search uses a much larger step than the configured
    /// sample rate.
    /// </summary>
    public static IAsyncEnumerable<SampledFrame> ReadEveryAsync(VideoMetadata video,
        PixelRegion region, int step, CancellationToken token = default, double startSeconds = 0,
        double? stopSeconds = null)
    {
        if (step < 1) throw new ArgumentException("Der Frameabstand muss mindestens 1 sein.");
        Check(video, region, startSeconds);
        var firstFrame = checked((int)Math.Ceiling(startSeconds * video.Fps / step) * step);
        var firstTimestamp = firstFrame / video.Fps;
        if (stopSeconds is { } stop && stop < firstTimestamp) return Empty();
        var arguments = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-ss", Number(firstTimestamp),
            "-i", video.Path, "-map", "0:v:0",
            "-vf", $"select=not(mod(n\\,{step})),{Crop(region)}",
            "-fps_mode", "passthrough", "-pix_fmt", "bgr24", "-f", "rawvideo",
        };
        // Half a frame of slack so the frame at the stop position is still delivered.
        if (stopSeconds is { } limit)
        {
            arguments.Add("-t");
            arguments.Add(Number(limit - firstTimestamp + 0.5 / video.Fps));
        }
        arguments.Add("-");
        return PipeAsync(video, region, arguments,
            index => (checked(firstFrame + index * step), firstTimestamp + index * step / video.Fps),
            token);
    }

    /// <summary>
    /// Streams the keyframes from a position, at most one per <paramref name="spacingSeconds"/>.
    /// FFmpeg decodes nothing in between, which is an order of magnitude faster than reading every
    /// frame. Each frame carries the timestamp FFmpeg reports for it, because a decoder of an
    /// intra-only codec ignores the request and hands out everything.
    /// </summary>
    public static async IAsyncEnumerable<SampledFrame> ReadKeyframesAsync(VideoMetadata video,
        PixelRegion region, [EnumeratorCancellation] CancellationToken token = default,
        double startSeconds = 0, double spacingSeconds = 0)
    {
        Check(video, region, startSeconds);
        var last = double.NegativeInfinity;
        await foreach (var frame in PipeAsync(video, region,
        [
            // showinfo writes one line per frame to stderr, which is where the timestamps below
            // come from; info level is needed for it, errors are collected all the same. Without
            // copyts the seek would rebase them to zero.
            "-nostdin", "-hide_banner", "-loglevel", "info", "-copyts", "-skip_frame", "nokey",
            "-ss", Number(startSeconds), "-i", video.Path, "-map", "0:v:0",
            "-vf", Crop(region) + ",showinfo", "-fps_mode", "passthrough", "-pix_fmt", "bgr24",
            "-f", "rawvideo", "-",
        ], null, token))
            if (frame.TimestampSeconds - last >= spacingSeconds)
            {
                last = frame.TimestampSeconds;
                yield return frame;
            }
            else frame.Dispose();
    }

    private static void Check(VideoMetadata video, PixelRegion region, double startSeconds)
    {
        if (region.Width <= 0 || region.Height <= 0 || region.X < 0 || region.Y < 0
            || region.X + region.Width > video.Width || region.Y + region.Height > video.Height)
            throw new ArgumentException("Der Bereich liegt außerhalb des Videos.");
        if (!double.IsFinite(startSeconds) || startSeconds < 0 || startSeconds >= video.DurationSeconds)
            throw new ArgumentException("Startzeit liegt außerhalb des Videos.");
    }

    // exact=1: otherwise crop rounds odd sizes and offsets down on subsampled input and every raw
    // frame would be smaller than the region.
    private static string Crop(PixelRegion region) =>
        $"crop={region.Width}:{region.Height}:{region.X}:{region.Y}:exact=1";

    private static async IAsyncEnumerable<SampledFrame> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// Runs FFmpeg and hands out its raw frames. <paramref name="position"/> gives each frame its
    /// number and timestamp and ends the stream by returning null; without it the timestamps are
    /// read from FFmpeg's own showinfo lines.
    /// </summary>
    private static async IAsyncEnumerable<SampledFrame> PipeAsync(VideoMetadata video,
        PixelRegion region, IEnumerable<string> arguments,
        Func<int, (int Number, double Timestamp)?>? position,
        [EnumeratorCancellation] CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(VideoService.Tool("ffmpeg"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var reported = System.Threading.Channels.Channel.CreateUnbounded<double>();
        var log = new System.Text.StringBuilder();
        var errors = DrainAsync(process, reported, log);
        try
        {
            var size = checked(region.Width * region.Height * 3);
            var buffer = new byte[size];
            // Everything the caller asked for was delivered; a caller that stops early instead
            // leaves the process to be killed below, and its exit code says nothing.
            var complete = true;
            for (var index = 0; ; index++)
            {
                (int Number, double Timestamp) at;
                if (position is not null)
                {
                    if (position(index) is not { } mapped) { complete = false; break; }
                    at = mapped;
                }
                else if (await Next(reported, token) is { } reportedTime)
                    at = ((int)Math.Round(reportedTime * video.Fps), reportedTime);
                else break;
                var read = await process.StandardOutput.BaseStream.ReadAtLeastAsync(
                    buffer, size, throwOnEndOfStream: false, token);
                if (read == 0) break;
                if (read < size)
                {
                    await process.WaitForExitAsync(CancellationToken.None);
                    await errors;
                    throw new IOException($"FFmpeg hat einen unvollständigen Frame geliefert "
                        + $"({read} von {size} Byte, Exit {process.ExitCode}): {log}");
                }
                var image = new Mat(region.Height, region.Width, MatType.CV_8UC3);
                Marshal.Copy(buffer, 0, image.Data, size);
                yield return new(at.Number, at.Timestamp, image);
            }
            if (!complete) yield break;
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0)
            {
                await errors;
                throw new IOException($"FFmpeg (Exit {process.ExitCode}): {log}");
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await errors;
        }
    }

    /// <summary>The next timestamp FFmpeg reported, or null once its output has ended.</summary>
    private static async ValueTask<double?> Next(
        System.Threading.Channels.Channel<double> reported, CancellationToken token)
    {
        try { return await reported.Reader.ReadAsync(token); }
        catch (System.Threading.Channels.ChannelClosedException) { return null; }
    }

    /// <summary>Keeps FFmpeg's stderr moving and picks the showinfo timestamps out of it.</summary>
    private static async Task DrainAsync(Process process,
        System.Threading.Channels.Channel<double> reported, System.Text.StringBuilder log)
    {
        const string marker = " pts_time:";
        while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
        {
            var start = line.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0 && line.Contains("showinfo", StringComparison.Ordinal))
            {
                var value = line[(start + marker.Length)..];
                var end = value.IndexOf(' ');
                if (double.TryParse(end < 0 ? value : value[..end],
                        System.Globalization.CultureInfo.InvariantCulture, out var timestamp))
                    reported.Writer.TryWrite(timestamp);
            }
            else if (log.Length < 4000) log.AppendLine(line);
        }
        reported.Writer.Complete();
    }

    private static string Number(double value) =>
        value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
}
