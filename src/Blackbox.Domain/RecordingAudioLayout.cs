namespace Blackbox.Domain;

public static class RecordingAudioLayout
{
    public static IReadOnlyList<AudioTrackExportSelection> CreateExportSelections(string layout)
    {
        return layout
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select((value, index) => new AudioTrackExportSelection(
                index,
                NormalizeTitle(
                    value.Contains(':') ? value[(value.IndexOf(':') + 1)..] : value,
                    index)))
            .ToArray();
    }

    public static string NormalizeTitle(string value, int streamIndex)
    {
        var trimmed = value.Trim();
        if (trimmed.Equals($"Track{streamIndex + 1}", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals($"Track {streamIndex + 1}", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals($"Audio track {streamIndex + 1}", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultTitle(streamIndex);
        }

        return trimmed.ToLowerInvariant() switch
        {
            "full_mix" => "Full listening mix",
            "game" => "Game audio",
            "voice" => "Voice chat",
            "raw_mic" => "Raw microphone",
            "processed_mic" => "Processed microphone",
            _ => trimmed
        };

    }

    public static string DefaultTitle(int streamIndex) => streamIndex switch
    {
        0 => "Full listening mix",
        1 => "Game audio",
        2 => "Voice chat",
        3 => "Raw microphone",
        4 => "Processed microphone",
        _ => $"Audio track {streamIndex + 1}"
    };
}
