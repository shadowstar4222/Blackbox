using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class LiveMarkerService(
    RecordingCoordinator recordingCoordinator,
    ISegmentRepository repository,
    TimelineMarkerOptions options,
    IClock clock,
    ILogger<LiveMarkerService> logger,
    IRecordingTimelineClock? timelineClock = null)
{
    public async Task<TimelineMarker> AddAsync(
        string? label = null,
        TimelineMarkerOrigin origin = TimelineMarkerOrigin.Live,
        string? reason = null,
        TimeSpan? contextBefore = null,
        TimeSpan? contextAfter = null,
        CancellationToken cancellationToken = default)
    {
        if (!recordingCoordinator.IsRecording)
        {
            throw new InvalidOperationException("Start recording before adding a live marker.");
        }

        options.Validate();
        var createdAt = timelineClock?.RecordingTime ?? clock.UtcNow;
        var marker = new TimelineMarker(
            Guid.NewGuid(),
            Guid.Empty,
            TimeSpan.Zero,
            string.IsNullOrWhiteSpace(label)
                ? $"Live marker {createdAt:HH:mm:ss}"
                : label.Trim(),
            createdAt,
            contextBefore ?? options.ContextBefore,
            contextAfter ?? options.ContextAfter,
            origin,
            reason);
        marker.Validate();
        await repository.AddMarkerAsync(marker, cancellationToken);
        logger.LogInformation(
            "Queued live timeline marker {MarkerId} at {MarkerTimestamp:o}.",
            marker.Id,
            marker.CreatedAt);
        return marker;
    }
}
