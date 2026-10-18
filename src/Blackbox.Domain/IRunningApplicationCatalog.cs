namespace Blackbox.Domain;

public interface IRunningApplicationCatalog
{
    Task<IReadOnlyList<RunningApplication>> GetRunningApplicationsAsync(
        CancellationToken cancellationToken = default);

    async Task<RunningApplication?> GetRunningApplicationAsync(long windowHandle,
        CancellationToken cancellationToken = default) =>
        (await GetRunningApplicationsAsync(cancellationToken)).FirstOrDefault(window => window.WindowHandle == windowHandle);
}
