namespace Blackbox.Domain;

public interface ISegmentUsageRegistry
{
    IDisposable Acquire(IReadOnlyCollection<Guid> segmentIds);
    bool IsInUse(Guid segmentId);
}
