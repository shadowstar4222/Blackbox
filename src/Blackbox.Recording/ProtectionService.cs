using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class ProtectionService(
    ISegmentRepository repository,
    IClock clock,
    ILogger<ProtectionService> logger)
{
    public Task ProtectPreviousFiveMinutesAsync(CancellationToken cancellationToken = default)
    {
        return ProtectPreviousAsync(TimeSpan.FromMinutes(5), cancellationToken);
    }

    public async Task ProtectPreviousAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Protection duration must be greater than zero.");
        }

        var endTime = clock.UtcNow;
        var startTime = endTime - duration;
        await repository.MarkProtectedRangeAsync(startTime, endTime, cancellationToken);
        logger.LogInformation(
            "Protected footage range {StartTime:o} to {EndTime:o}.",
            startTime,
            endTime);
    }
}
