namespace Blackbox.Infrastructure;

public sealed record SupportBundleOptions
{
    public int MaximumLogEntries { get; init; } = 500;
}
