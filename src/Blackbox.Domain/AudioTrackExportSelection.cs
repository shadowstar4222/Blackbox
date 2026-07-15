namespace Blackbox.Domain;

public sealed record AudioTrackExportSelection(
    int StreamIndex,
    string Name,
    bool IsMuted = false,
    bool IsSolo = false,
    double Volume = 1,
    bool ExportAsWav = false);
