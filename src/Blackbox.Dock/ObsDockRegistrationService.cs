using System.Text.Json;
using System.Text.Json.Nodes;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.Dock;

public sealed record ObsDockRegistrationResult(
    string ConfigurationPath,
    bool WasChanged,
    Uri DockUrl);

public sealed class ObsDockRegistrationService(
    ObsDockSettingsStore settingsStore,
    ILogger<ObsDockRegistrationService> logger) : IObsPortableConfigurationContributor
{
    internal const string DockUuid = "5d27a418e12f4db5a4059ccdb66ce11d";
    private const string DockTitle = "Blackbox Controls";

    public void Apply(string portableRootDirectory) => Register(portableRootDirectory);

    public ObsDockRegistrationResult Register(string portableRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portableRootDirectory);
        var dockUrl = settingsStore.GetDockUri();
        var configurationPath = Path.Combine(
            Path.GetFullPath(portableRootDirectory),
            "config",
            "obs-studio",
            "user.ini");
        var directory = Path.GetDirectoryName(configurationPath)!;
        Directory.CreateDirectory(directory);
        if (!File.Exists(configurationPath))
        {
            File.WriteAllText(
                configurationPath,
                "[General]\r\nFirstRun=true\r\n\r\n[BasicWindow]\r\nSysTrayEnabled=true\r\nSysTrayWhenStarted=true\r\nPreviewEnabled=false\r\n");
        }

        var original = File.ReadAllText(configurationPath);
        var updated = UpsertDock(original, dockUrl);
        if (updated.Equals(original, StringComparison.Ordinal))
        {
            return new ObsDockRegistrationResult(configurationPath, false, dockUrl);
        }

        WriteAtomically(configurationPath, original, updated);
        logger.LogInformation(
            "Registered the Blackbox OBS dock in managed portable configuration {ConfigurationPath}.",
            configurationPath);
        return new ObsDockRegistrationResult(configurationPath, true, dockUrl);
    }

    internal static string UpsertDock(string configuration, Uri dockUrl)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dockUrl);
        if (!dockUrl.IsLoopback || dockUrl.Scheme != Uri.UriSchemeHttp)
        {
            throw new InvalidDataException("The OBS dock URL must use local HTTP only.");
        }

        var newline = configuration.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hadTrailingNewline = configuration.EndsWith('\n');
        var lines = configuration
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();
        if (hadTrailingNewline && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var sectionStart = FindSection(lines, "BasicWindow");
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add("[BasicWindow]");
            sectionStart = lines.Count - 1;
        }

        var sectionEnd = FindSectionEnd(lines, sectionStart);
        var valueIndex = FindValue(lines, sectionStart + 1, sectionEnd, "ExtraBrowserDocks");
        var existingJson = valueIndex >= 0
            ? lines[valueIndex][(lines[valueIndex].IndexOf('=') + 1)..].Trim()
            : string.Empty;
        var docks = ParseDocks(existingJson);
        RemoveBlackboxDock(docks);
        docks.Add(new JsonObject
        {
            ["title"] = DockTitle,
            ["url"] = dockUrl.AbsoluteUri,
            ["uuid"] = DockUuid
        });
        var serialized = docks.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var value = $"ExtraBrowserDocks={serialized}";
        if (valueIndex >= 0)
        {
            lines[valueIndex] = value;
        }
        else
        {
            lines.Insert(sectionEnd, value);
        }

        var result = string.Join(newline, lines);
        return hadTrailingNewline ? result + newline : result;
    }

    internal static bool ContainsRegisteredDock(string configuration, Uri expectedUrl)
    {
        var lines = configuration
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        var sectionStart = FindSection(lines, "BasicWindow");
        if (sectionStart < 0)
        {
            return false;
        }

        var sectionEnd = FindSectionEnd(lines, sectionStart);
        var valueIndex = FindValue(lines, sectionStart + 1, sectionEnd, "ExtraBrowserDocks");
        if (valueIndex < 0)
        {
            return false;
        }

        var json = lines[valueIndex][(lines[valueIndex].IndexOf('=') + 1)..].Trim();
        var docks = ParseDocks(json);
        return docks.OfType<JsonObject>().Any(dock =>
            string.Equals(
                NormalizeUuid(ReadString(dock, "uuid")),
                DockUuid,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ReadString(dock, "url"), expectedUrl.AbsoluteUri, StringComparison.Ordinal));
    }

    private static JsonArray ParseDocks(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(json) as JsonArray
                ?? throw new InvalidDataException("OBS ExtraBrowserDocks is not a JSON array.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "OBS ExtraBrowserDocks contains malformed JSON. Blackbox left it unchanged.",
                ex);
        }
    }

    private static void RemoveBlackboxDock(JsonArray docks)
    {
        for (var index = docks.Count - 1; index >= 0; index--)
        {
            if (docks[index] is JsonObject dock &&
                string.Equals(
                    NormalizeUuid(ReadString(dock, "uuid")),
                    DockUuid,
                    StringComparison.OrdinalIgnoreCase))
            {
                docks.RemoveAt(index);
            }
        }
    }

    private static string? ReadString(JsonObject value, string propertyName) =>
        value[propertyName] is JsonValue jsonValue &&
        jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;

    private static string? NormalizeUuid(string? value) =>
        value?.Replace("-", string.Empty, StringComparison.Ordinal).Trim('{', '}');

    private static int FindSection(IReadOnlyList<string> lines, string sectionName)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Trim().Equals($"[{sectionName}]", StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindSectionEnd(IReadOnlyList<string> lines, int sectionStart)
    {
        for (var index = sectionStart + 1; index < lines.Count; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) &&
                line.EndsWith("]", StringComparison.Ordinal))
            {
                return index;
            }
        }

        return lines.Count;
    }

    private static int FindValue(
        IReadOnlyList<string> lines,
        int start,
        int end,
        string key)
    {
        for (var index = start; index < end; index++)
        {
            var separator = lines[index].IndexOf('=');
            if (separator > 0 &&
                lines[index][..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static void WriteAtomically(string path, string original, string updated)
    {
        var directory = Path.GetDirectoryName(path)!;
        var backupPath = path + ".blackbox-backup";
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, updated);
            File.WriteAllText(backupPath, original);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
