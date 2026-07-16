namespace Blackbox.Infrastructure;

public sealed record DiagnosticLogOptions
{
    public required string LogDirectory { get; init; }
}
