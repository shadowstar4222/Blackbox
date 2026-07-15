namespace Blackbox.Domain;

public sealed record TimelineAssets(
    IReadOnlyList<TimelineThumbnail> Thumbnails,
    IReadOnlyList<double> Waveform,
    bool LoadedFromCache);
