using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Export;

public sealed class MarkerRetentionService(
    ISegmentRepository segmentRepository,
    IClipRepository clipRepository,
    RecordingSessionCatalog sessionCatalog,
    RecordingLibraryService recordingLibrary,
    InstantClipOptions options,
    IClock clock,
    ILogger<MarkerRetentionService> logger)
{
    private DateTimeOffset _lastLibraryRefresh = DateTimeOffset.MinValue;
    private readonly Dictionary<Guid, HashSet<Guid>> _sourceSegments = [];
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private long? _reconciledRevision;
    private bool _hasNoMarkers;

    public async Task ReconcileAsync(
        bool recordingActive,
        CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken);
        try
        {
            var revision = segmentRepository.Revision;
            if (revision is not null && revision == _reconciledRevision &&
                (!recordingActive || _hasNoMarkers))
            {
                return;
            }

            await ReconcileCoreAsync(recordingActive, cancellationToken);
            _reconciledRevision = revision;
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task ReconcileCoreAsync(bool recordingActive, CancellationToken cancellationToken)
    {
        var markers = await segmentRepository.GetMarkersAsync(cancellationToken);
        _hasNoMarkers = markers.Count == 0;
        if (markers.Count == 0)
        {
            _sourceSegments.Clear();
            return;
        }

        var sessions = await sessionCatalog.GetSessionsAsync(cancellationToken);
        var sessionsById = sessions.ToDictionary(static session => session.Id);
        var needsRefresh =
            markers.Any(static marker => marker.SessionId == Guid.Empty) ||
            recordingActive && markers.Any(marker =>
            {
                return sessionsById.TryGetValue(marker.SessionId, out var session) &&
                    RecordingTimeline.ToTimestamp(session, marker.Offset) + marker.ContextAfter >
                    session.EndTime;
            });
        if (needsRefresh &&
            clock.UtcNow - _lastLibraryRefresh >= options.LibraryRefreshInterval)
        {
            sessions = await recordingLibrary.RefreshAsync(cancellationToken: cancellationToken);
            markers = await segmentRepository.GetMarkersAsync(cancellationToken);
            sessionsById = sessions.ToDictionary(static session => session.Id);
            _lastLibraryRefresh = clock.UtcNow;
        }

        foreach (var marker in markers.Where(static marker => marker.SessionId != Guid.Empty))
        {
            if (!sessionsById.TryGetValue(marker.SessionId, out var session))
            {
                continue;
            }

            var markerTime = RecordingTimeline.ToTimestamp(session, marker.Offset);
            var rangeStart = markerTime - marker.ContextBefore;
            var rangeEnd = markerTime + marker.ContextAfter;
            var sourceIds = session.Segments
                .Where(segment => segment.StartTime < rangeEnd && segment.EndTime > rangeStart)
                .Select(static segment => segment.Id)
                .ToArray();
            if (_sourceSegments.TryGetValue(marker.Id, out var existing) && existing.SetEquals(sourceIds))
            {
                continue;
            }

            await clipRepository.SetMarkerSourceSegmentsAsync(
                marker.Id,
                sourceIds,
                cancellationToken);
            _sourceSegments[marker.Id] = sourceIds.ToHashSet();
        }

        var markerIds = markers.Select(static marker => marker.Id).ToHashSet();
        foreach (var id in _sourceSegments.Keys.Where(id => !markerIds.Contains(id)).ToArray())
        {
            _sourceSegments.Remove(id);
        }

        logger.LogDebug(
            "Reconciled source ownership for {MarkerCount} timeline marker(s).",
            markers.Count(static marker => marker.SessionId != Guid.Empty));
    }
}
