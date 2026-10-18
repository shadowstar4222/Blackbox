namespace Blackbox.Export;

public sealed record FfmpegOptions
{
    public required string RootDirectory { get; init; }
    public required string WorkDirectory { get; init; }
    public string? TimelineCacheDirectory { get; init; }
    public long TimelineCacheMaximumBytes { get; init; } = 1024L * 1024 * 1024;
    public TimeSpan TimelineCacheMaximumAge { get; init; } = TimeSpan.FromDays(30);
}
