using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Blackbox.Export;

public sealed class FfmpegCommandRunner(ILogger<FfmpegCommandRunner> logger) : IFfmpegCommandRunner
{
    public async Task RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan expectedDuration,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start FFmpeg.");
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
        var errors = new ConcurrentQueue<string>();
        var errorTask = ReadErrorsAsync(process, errors, cancellationToken);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) &&
                expectedDuration > TimeSpan.Zero)
            {
                progress?.Report(Math.Clamp(microseconds / 1_000_000d / expectedDuration.TotalSeconds * 100, 0, 99));
            }
            else if (line == "progress=end")
            {
                progress?.Report(100);
            }
        }

        await process.WaitForExitAsync(cancellationToken);
        await errorTask;
        if (process.ExitCode != 0)
        {
            var details = string.Join(Environment.NewLine, errors.TakeLast(20));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(details)
                    ? $"FFmpeg exited with code {process.ExitCode}."
                    : details);
        }

        logger.LogInformation("FFmpeg completed successfully with {ArgumentCount} argument(s).", arguments.Count);
    }

    private static async Task ReadErrorsAsync(
        Process process,
        ConcurrentQueue<string> errors,
        CancellationToken cancellationToken)
    {
        while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                errors.Enqueue(line);
                while (errors.Count > 100)
                {
                    errors.TryDequeue(out _);
                }
            }
        }
    }
}
