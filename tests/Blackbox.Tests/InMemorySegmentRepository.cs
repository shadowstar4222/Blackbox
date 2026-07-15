using Blackbox.Domain;

namespace Blackbox.Tests;

internal sealed class InMemorySegmentRepository : ISegmentRepository
{
    private readonly List<RecordingSegment> _segments = [];

    public bool Initialized { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Initialized = true;
        return Task.CompletedTask;
    }

    public Task UpsertAsync(RecordingSegment segment, CancellationToken cancellationToken = default)
    {
        _segments.RemoveAll(existing => existing.FilePath == segment.FilePath);
        _segments.Add(segment);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RecordingSegment>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<RecordingSegment>>(_segments);
    }

    public Task<bool> ExistsByPathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_segments.Any(segment => segment.FilePath == filePath));
    }

    public Task MarkProtectedRangeAsync(DateTimeOffset startTime, DateTimeOffset endTime, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < _segments.Count; i++)
        {
            var segment = _segments[i];
            if (segment.StartTime < endTime && segment.EndTime > startTime)
            {
                _segments[i] = segment with { IsProtected = true };
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _segments.RemoveAll(segment => segment.Id == id);
        return Task.CompletedTask;
    }

    public Task ReconcileMissingFilesAsync(CancellationToken cancellationToken = default)
    {
        _segments.RemoveAll(segment => !File.Exists(segment.FilePath));
        return Task.CompletedTask;
    }
}
