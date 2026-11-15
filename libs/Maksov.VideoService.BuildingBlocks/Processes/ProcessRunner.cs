using System.Diagnostics;
using System.Text;

namespace Maksov.VideoService.BuildingBlocks.Processes;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErrTail);

/// <summary>
/// Runs an external process without a shell: arguments via ArgumentList (no injection),
/// timeout kills the process tree, stderr keeps only the tail (no megabytes of logs),
/// per-line callbacks to parse stdout/stderr on the fly.
/// </summary>
public static class ProcessRunner
{
    private const int StdErrTailChars = 4096;

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Action<string>? onStdOutLine,
        bool captureStdOut,
        CancellationToken cancellationToken,
        Action<string>? onStdErrLine = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdOutTask = ReadStdOutAsync(process.StandardOutput, onStdOutLine, captureStdOut);
        var stdErrTask = ReadStdErrAsync(process.StandardError, onStdErrLine);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdOutTask, stdErrTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(fileName)} exceeded {timeout.TotalSeconds:F0}s");
        }

        return new ProcessResult(process.ExitCode, await stdOutTask.ConfigureAwait(false), await stdErrTask.ConfigureAwait(false));
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static async Task<string> ReadStdOutAsync(StreamReader reader, Action<string>? onLine, bool capture)
    {
        var buffer = capture ? new StringBuilder() : null;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            onLine?.Invoke(line);
            buffer?.AppendLine(line);
        }
        return buffer?.ToString() ?? "";
    }

    private static async Task<string> ReadStdErrAsync(StreamReader reader, Action<string>? onLine)
    {
        var tail = new StringBuilder(StdErrTailChars * 2);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            onLine?.Invoke(line);
            tail.AppendLine(line);
            if (tail.Length > StdErrTailChars * 2)
            {
                tail.Remove(0, tail.Length - StdErrTailChars);
            }
        }
        return tail.Length > StdErrTailChars ? tail.ToString(tail.Length - StdErrTailChars, StdErrTailChars) : tail.ToString();
    }
}
