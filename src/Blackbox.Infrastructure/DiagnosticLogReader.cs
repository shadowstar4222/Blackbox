using System.Globalization;
using System.Text.RegularExpressions;
using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public sealed partial class DiagnosticLogReader(DiagnosticLogOptions options) : IDiagnosticLogReader
{
    public async Task<IReadOnlyList<DiagnosticLogEntry>> GetRecentAsync(
        int maximumEntries = 300,
        CancellationToken cancellationToken = default)
    {
        if (maximumEntries is < 1 or > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        if (options.MaximumFiles is < 1 or > 30)
        {
            throw new InvalidOperationException("Diagnostic log file count must be between 1 and 30.");
        }

        if (options.MaximumBytesPerFile is < 4096 or > 16 * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "Diagnostic log read size must be between 4 KB and 16 MB per file.");
        }

        if (!Directory.Exists(options.LogDirectory))
        {
            return [];
        }

        var entries = new List<DiagnosticLogEntry>();
        IReadOnlyList<FileInfo> files;
        try
        {
            files = Directory
                .EnumerateFiles(options.LogDirectory, "blackbox-*.log", SearchOption.TopDirectoryOnly)
                .Select(static path => new FileInfo(path))
                .OrderByDescending(static file => file.LastWriteTimeUtc)
                .Take(options.MaximumFiles)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string> lines;
            try
            {
                lines = await ReadSharedTailLinesAsync(
                    file.FullName,
                    maximumEntries,
                    options.MaximumBytesPerFile,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var line in lines)
            {
                if (TryParse(line, file.Name, out var entry))
                {
                    entries.Add(entry);
                }
            }

            if (entries.Count >= maximumEntries)
            {
                break;
            }
        }

        return entries
            .OrderByDescending(static entry => entry.Timestamp)
            .Take(maximumEntries)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> ReadSharedTailLinesAsync(
        string path,
        int maximumLines,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var startOffset = Math.Max(0, stream.Length - maximumBytes);
        if (startOffset > 0)
        {
            stream.Seek(startOffset, SeekOrigin.Begin);
        }

        using var reader = new StreamReader(stream);
        if (startOffset > 0)
        {
            _ = await reader.ReadLineAsync(cancellationToken);
        }

        var lines = new Queue<string>(maximumLines);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (lines.Count == maximumLines)
            {
                lines.Dequeue();
            }

            lines.Enqueue(line);
        }

        return lines.ToArray();
    }

    internal static bool TryParse(string line, string sourceFile, out DiagnosticLogEntry entry)
    {
        var match = LogLinePattern().Match(line);
        if (!match.Success ||
            !DateTimeOffset.TryParse(
                match.Groups["timestamp"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var timestamp))
        {
            entry = default!;
            return false;
        }

        var message = match.Groups["message"].Value.Trim();
        entry = new DiagnosticLogEntry(
            timestamp,
            Classify(message),
            ParseSeverity(match.Groups["level"].Value),
            message,
            sourceFile);
        return true;
    }

    private static DiagnosticCategory Classify(string message)
    {
        if (ContainsAny(message, "recover", "damaged", "reconcil", "unreadable"))
        {
            return DiagnosticCategory.Recovery;
        }

        if (ContainsAny(message, "automatic capture", "detected", "game capture", "remembered game", "game window"))
        {
            return DiagnosticCategory.Detection;
        }

        if (ContainsAny(message, "export", "ffmpeg", "timeline", "playback"))
        {
            return DiagnosticCategory.Export;
        }

        return ContainsAny(message, "recording", "obs", "microphone")
            ? DiagnosticCategory.Recording
            : DiagnosticCategory.System;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static DiagnosticSeverity ParseSeverity(string value) => value switch
    {
        "DBG" or "VRB" => DiagnosticSeverity.Debug,
        "WRN" => DiagnosticSeverity.Warning,
        "ERR" or "FTL" => DiagnosticSeverity.Error,
        _ => DiagnosticSeverity.Information
    };

    [GeneratedRegex(
        "^(?<timestamp>\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}\\.\\d{3} [+-]\\d{2}:\\d{2}) \\[(?<level>[A-Z]{3})\\] (?<message>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LogLinePattern();
}
