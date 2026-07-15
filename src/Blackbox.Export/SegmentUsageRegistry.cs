using Blackbox.Domain;

namespace Blackbox.Export;

public sealed class SegmentUsageRegistry : ISegmentUsageRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, int> _usageCounts = [];

    public IDisposable Acquire(IReadOnlyCollection<Guid> segmentIds)
    {
        var ids = segmentIds.Distinct().ToArray();
        lock (_sync)
        {
            foreach (var id in ids)
            {
                _usageCounts[id] = _usageCounts.GetValueOrDefault(id) + 1;
            }
        }

        return new UsageLease(this, ids);
    }

    public bool IsInUse(Guid segmentId)
    {
        lock (_sync)
        {
            return _usageCounts.ContainsKey(segmentId);
        }
    }

    private void Release(IReadOnlyList<Guid> segmentIds)
    {
        lock (_sync)
        {
            foreach (var id in segmentIds)
            {
                var remaining = _usageCounts.GetValueOrDefault(id) - 1;
                if (remaining <= 0)
                {
                    _usageCounts.Remove(id);
                }
                else
                {
                    _usageCounts[id] = remaining;
                }
            }
        }
    }

    private sealed class UsageLease(SegmentUsageRegistry registry, IReadOnlyList<Guid> segmentIds) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                registry.Release(segmentIds);
            }
        }
    }
}
