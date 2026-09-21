using System.Diagnostics;
using System.Text;

namespace Bf6Highlights;

public static class MediaProcess
{
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        }};
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            if (!cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"{executable}: Zeitlimit überschritten.");
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new IOException($"{executable} (Exit {process.ExitCode}): {await stderr}");
        return await stdout;
    }

    // Keep draining both pipes, while bounding retained output for long-running tools.
    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            text.Append(buffer, 0, count);
            if (text.Length > 262144) text.Remove(0, text.Length - 262144);
        }
        return text.ToString();
    }
}
