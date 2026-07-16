namespace Blackbox.Domain;

public sealed record GpuActivitySnapshot(
    bool IsAvailable,
    IReadOnlyDictionary<int, double> UtilizationByProcessId)
{
    public double GetUtilization(int processId) =>
        UtilizationByProcessId.TryGetValue(processId, out var utilization) ? utilization : 0;
}
