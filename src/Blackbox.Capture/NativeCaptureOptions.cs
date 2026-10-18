namespace Blackbox.Capture;

public sealed record NativeCaptureOptions
{
    public string WorkerPath { get; init; } = Path.Combine(
        AppContext.BaseDirectory, "capture", "Blackbox.Capture.Worker.exe");
    public string FfmpegPath { get; init; } = Path.Combine(
        AppContext.BaseDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe");
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
