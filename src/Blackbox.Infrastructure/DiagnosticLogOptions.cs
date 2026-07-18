namespace Blackbox.Infrastructure;

public sealed record DiagnosticLogOptions
{
    public required string LogDirectory { get; init; }
    public int MaximumFiles { get; init; } = 7;
    public long MaximumBytesPerFile { get; init; } = 2 * 1024 * 1024;
}
