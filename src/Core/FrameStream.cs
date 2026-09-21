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
    public static async IAsyncEnumerable<SampledFrame> ReadAsync(VideoMetadata video, PixelRegion region,
        int samplesPerSecond, [EnumeratorCancellation] CancellationToken token = default)
    {
        if (region.Width <= 0 || region.Height <= 0 || region.X < 0 || region.Y < 0
            || region.X + region.Width > video.Width || region.Y + region.Height > video.Height)
            throw new ArgumentException("Der Bereich liegt außerhalb des Videos.");
        var step = ChangeDetector.SampleStep(video.Fps, samplesPerSecond);
        var filter = $"select=not(mod(n\\,{step})),"
            // exact=1: otherwise crop rounds odd sizes and offsets down on subsampled input and
            // every raw frame would be smaller than the region.
            + $"crop={region.Width}:{region.Height}:{region.X}:{region.Y}:exact=1";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(VideoService.Tool("ffmpeg"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            },
        };
        foreach (var argument in new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-i", video.Path, "-map", "0:v:0",
            "-vf", filter, "-fps_mode", "passthrough", "-pix_fmt", "bgr24", "-f", "rawvideo", "-",
        }) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            var size = checked(region.Width * region.Height * 3);
            var buffer = new byte[size];
            for (var index = 0; ; index++)
            {
                var read = await process.StandardOutput.BaseStream.ReadAtLeastAsync(
                    buffer, size, throwOnEndOfStream: false, token);
                if (read == 0) break;
                if (read < size)
                {
                    await process.WaitForExitAsync(CancellationToken.None);
                    throw new IOException($"FFmpeg hat einen unvollständigen Frame geliefert "
                        + $"({read} von {size} Byte, Exit {process.ExitCode}): {await errors}");
                }
                var number = checked(index * step);
                var image = new Mat(region.Height, region.Width, MatType.CV_8UC3);
                Marshal.Copy(buffer, 0, image.Data, size);
                yield return new(number, number / video.Fps, image);
            }
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0)
                throw new IOException($"FFmpeg (Exit {process.ExitCode}): {await errors}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await errors;
        }
    }
}
