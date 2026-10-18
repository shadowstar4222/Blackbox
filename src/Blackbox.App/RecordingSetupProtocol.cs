using System.IO;
using System.Text.Json;

namespace Blackbox.App;

internal sealed record RecordingSetupConfiguration(int ParentId, long ParentStartTicks, string Directory,
    bool Moving, int SceneFps, string MusicPath, bool Validate = false, bool ValidateAudio = false);

internal sealed record RecordingSetupStatus(string State, string Video, string Audio, string Microphone,
    string Processing, string Message, bool CanRecord, bool CanStop, bool AutoRecord = false);

internal sealed record RecordingSetupSceneState(int ProcessId, bool Started, bool Ready)
{
    internal bool IsReadyFor(int processId) => processId > 0 && ProcessId == processId && Started && Ready;
}

internal sealed record RecordingSetupCommand(string Id, string Action);

internal static class RecordingSetupProtocol
{
    internal const string Warning = "PHOTOSENSITIVITY WARNING\nBright, high-contrast patterns and motion may trigger seizures or discomfort. Stop immediately if you feel unwell. Still color bars are available, but are not guaranteed safe.";

    internal static void Write<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static T? Read<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }
}
