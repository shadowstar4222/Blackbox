namespace Blackbox.Domain;

public interface IDiagnosticLogReader
{
    Task<IReadOnlyList<DiagnosticLogEntry>> GetRecentAsync(
        int maximumEntries = 300,
        CancellationToken cancellationToken = default);
}
