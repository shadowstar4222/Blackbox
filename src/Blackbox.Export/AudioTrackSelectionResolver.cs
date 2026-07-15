using Blackbox.Domain;

namespace Blackbox.Export;

internal static class AudioTrackSelectionResolver
{
    public static IReadOnlyList<AudioTrackExportSelection> Resolve(SessionExportRequest request)
    {
        return request.AudioTracks?.OrderBy(static track => track.StreamIndex).ToArray()
            ?? ParseLayout(request.Session.Segments[0].AudioTrackLayout);
    }

    public static IReadOnlyList<AudioTrackExportSelection> Audible(
        IReadOnlyList<AudioTrackExportSelection> tracks)
    {
        var hasSolo = tracks.Any(static track => track.IsSolo && !track.IsMuted);
        return tracks
            .Where(track => !track.IsMuted && (!hasSolo || track.IsSolo))
            .OrderBy(static track => track.StreamIndex)
            .ToArray();
    }

    public static bool IsDefault(
        SessionExportRequest request,
        IReadOnlyList<AudioTrackExportSelection> tracks)
    {
        var defaults = ParseLayout(request.Session.Segments[0].AudioTrackLayout);
        return tracks.Count == defaults.Count && tracks.Zip(defaults).All(pair =>
            pair.First.StreamIndex == pair.Second.StreamIndex &&
            !pair.First.IsMuted &&
            !pair.First.IsSolo &&
            Math.Abs(pair.First.Volume - 1) < 0.0001);
    }

    public static IReadOnlyList<AudioTrackExportSelection> ParseLayout(string layout)
        => RecordingAudioLayout.CreateExportSelections(layout);
}
