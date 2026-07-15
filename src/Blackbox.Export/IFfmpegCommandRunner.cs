namespace Blackbox.Export;

public interface IFfmpegCommandRunner
{
    Task RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan expectedDuration,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
