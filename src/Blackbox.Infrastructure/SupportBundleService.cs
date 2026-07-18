using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public sealed partial class SupportBundleService(
    IClock clock,
    SupportBundleOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public const string PrivacyDisclosure =
        "Included: Blackbox and Windows/.NET versions, recording-state counters, recovery summary, " +
        "and a capped sample of recent event messages. Event messages are redacted for credentials, " +
        "user-profile paths, URI credentials, and microphone device identifiers.\n\n" +
        "Excluded: video/audio recordings, screenshots, the Blackbox database, OBS passwords, " +
        "microphone configuration, saved game profiles, executable lists, and settings files.";

    public async Task<SupportBundleResult> ExportAsync(
        string destinationPath,
        SupportBundleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(request);
        if (options.MaximumLogEntries is < 1 or > 2000)
        {
            throw new InvalidOperationException("Support bundle log entry count must be between 1 and 2000.");
        }

        var fullPath = Path.GetFullPath(destinationPath);
        var parentDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The support bundle path has no parent directory.");
        Directory.CreateDirectory(parentDirectory);
        var temporaryPath = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.partial");
        var redactionCount = 0;
        var entries = request.LogEntries
            .OrderByDescending(static entry => entry.Timestamp)
            .Take(options.MaximumLogEntries)
            .Select(entry => new SupportEvent(
                entry.Timestamp,
                entry.Category.ToString(),
                entry.Severity.ToString(),
                Redact(entry.Message, ref redactionCount),
                Path.GetFileName(entry.SourceFile)))
            .ToArray();
        var manifest = CreateManifest(request, entries.Length, ref redactionCount);

        try
        {
            await using (var file = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                await WriteJsonEntryAsync(
                    archive,
                    "diagnostics.json",
                    manifest,
                    cancellationToken);
                await WriteJsonEntryAsync(
                    archive,
                    "recent-events.json",
                    entries,
                    cancellationToken);
                await WriteTextEntryAsync(
                    archive,
                    "PRIVACY.txt",
                    PrivacyDisclosure + Environment.NewLine,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, true);
            return new SupportBundleResult(
                fullPath,
                new FileInfo(fullPath).Length,
                entries.Length,
                redactionCount);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private SupportManifest CreateManifest(
        SupportBundleRequest request,
        int includedLogEntries,
        ref int redactionCount)
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";
        return new SupportManifest(
            1,
            clock.UtcNow,
            new ApplicationDetails(
                version,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture.ToString(),
                RuntimeInformation.ProcessArchitecture.ToString()),
            new HealthDetails(
                request.IsRecording,
                request.IsAutomaticCaptureEnabled,
                request.IndexedSegments,
                request.DamagedSegments,
                request.MissingSegments,
                Math.Max(0, request.RecordingBytes),
                request.PreservedRecoveryFiles,
                Redact(request.RecoverySummary, ref redactionCount)),
            new PrivacyDetails(
                includedLogEntries,
                redactionCount,
                [
                    "Video and audio recordings",
                    "Screenshots",
                    "Blackbox database",
                    "OBS passwords and settings",
                    "Microphone configuration and device identifiers",
                    "Saved game profiles and executable lists"
                ]));
    }

    private static async Task WriteJsonEntryAsync<T>(
        ZipArchive archive,
        string name,
        T value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }

    private static async Task WriteTextEntryAsync(
        ZipArchive archive,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false);
        await writer.WriteAsync(value.AsMemory(), cancellationToken);
    }

    private static string Redact(string value, ref int redactionCount)
    {
        var redacted = ReplaceAndCount(UserProfilePathPattern(), value, "%USERPROFILE%", ref redactionCount);
        redacted = ReplaceAndCount(UriCredentialPattern(), redacted, "<credentials>@", ref redactionCount);
        redacted = ReplaceAndCount(
            SensitiveAssignmentPattern(),
            redacted,
            match => $"{match.Groups["name"].Value}{match.Groups["separator"].Value}<redacted>",
            ref redactionCount);
        return ReplaceAndCount(
            MicrophoneIdentifierPattern(),
            redacted,
            match => $"{match.Groups["prefix"].Value}<device>",
            ref redactionCount);
    }

    private static string ReplaceAndCount(
        Regex pattern,
        string value,
        string replacement,
        ref int redactionCount)
    {
        var matches = pattern.Matches(value).Count;
        redactionCount += matches;
        return matches == 0 ? value : pattern.Replace(value, replacement);
    }

    private static string ReplaceAndCount(
        Regex pattern,
        string value,
        MatchEvaluator replacement,
        ref int redactionCount)
    {
        var matches = pattern.Matches(value).Count;
        redactionCount += matches;
        return matches == 0 ? value : pattern.Replace(value, replacement);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(
        @"(?i)\b[A-Z]:\\Users\\[^\\\s""']+",
        RegexOptions.CultureInvariant)]
    private static partial Regex UserProfilePathPattern();

    [GeneratedRegex(
        @"(?i)(?<=://)[^/\s:@]+:[^@\s/]+@",
        RegexOptions.CultureInvariant)]
    private static partial Regex UriCredentialPattern();

    [GeneratedRegex(
        @"(?i)\b(?<name>password|passwd|token|secret|authorization|api[-_ ]?key)(?<separator>\s*[:=]\s*)(?:""[^""]*""|'[^']*'|\S+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignmentPattern();

    [GeneratedRegex(
        @"(?i)(?<prefix>\bmicrophone\s+)(?:\{?[A-F0-9-]{8,}\}?|[^\s]*[#{][^\s]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex MicrophoneIdentifierPattern();

    private sealed record SupportManifest(
        int SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        ApplicationDetails Application,
        HealthDetails Health,
        PrivacyDetails Privacy);

    private sealed record ApplicationDetails(
        string Version,
        string DotnetRuntime,
        string OperatingSystem,
        string OsArchitecture,
        string ProcessArchitecture);

    private sealed record HealthDetails(
        bool IsRecording,
        bool IsAutomaticCaptureEnabled,
        int IndexedSegments,
        int DamagedSegments,
        int MissingSegments,
        long RecordingBytes,
        int PreservedRecoveryFiles,
        string RecoverySummary);

    private sealed record PrivacyDetails(
        int IncludedLogEntries,
        int RedactionCount,
        IReadOnlyList<string> ExcludedData);

    private sealed record SupportEvent(
        DateTimeOffset Timestamp,
        string Category,
        string Severity,
        string Message,
        string SourceFile);
}
