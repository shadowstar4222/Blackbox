namespace Blackbox.Domain;

public interface IGpuActivityProbe
{
    Task<GpuActivitySnapshot> SampleAsync(
        IReadOnlyCollection<int> processIds,
        CancellationToken cancellationToken = default);
}
