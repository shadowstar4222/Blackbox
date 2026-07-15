namespace Blackbox.Export;

public sealed record FfmpegOptions
{
    public required string RootDirectory { get; init; }
    public required string WorkDirectory { get; init; }
    public Uri PackageUri { get; init; } = new("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip");
    public Uri ChecksumUri { get; init; } = new("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256");
}
