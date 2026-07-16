namespace Blackbox.Domain;

public interface IRunningApplicationCatalog
{
    Task<IReadOnlyList<RunningApplication>> GetRunningApplicationsAsync(
        CancellationToken cancellationToken = default);
}
