using Blackbox.Domain;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class AutomaticMarkerService(
    AutomaticMarkerEvaluator evaluator,
    GameProfileSettingsService profileSettings,
    IRecordingGuardianContext guardianContext,
    LiveMarkerService liveMarkerService,
    ILogger<AutomaticMarkerService> logger)
{
    private readonly Dictionary<string, AutomaticMarkerSignal> _previousSignals =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastMarkers =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task ObserveAsync(
        RecordingGuardianSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var target = guardianContext.Target;
        if (!guardianContext.IsRecording ||
            !snapshot.RecordingOutputActive ||
            target is null ||
            string.IsNullOrWhiteSpace(snapshot.TargetTitle))
        {
            return;
        }

        var effective = await profileSettings.ResolveAsync(target, cancellationToken);
        var settings = effective.AutomaticMarkers;
        settings.Validate();
        var key = Path.GetFullPath(target.ExecutablePath);
        var signal = new AutomaticMarkerSignal(
            snapshot.CapturedAt,
            snapshot.TargetTitle,
            snapshot.GameAudioSampleCount > 0 ? snapshot.GameAudioPeakDb : null,
            snapshot.Frame?.MeanLuminance);
        _previousSignals.TryGetValue(key, out var previous);
        _lastMarkers.TryGetValue(key, out var lastMarkerAt);
        var decision = evaluator.Evaluate(
            signal,
            previous,
            lastMarkerAt == default ? null : lastMarkerAt,
            settings);
        _previousSignals[key] = signal;
        if (decision is null)
        {
            return;
        }

        await liveMarkerService.AddAsync(
            decision.Label,
            decision.Origin,
            decision.Reason,
            settings.ContextBefore,
            settings.ContextAfter,
            cancellationToken);
        _lastMarkers[key] = snapshot.CapturedAt;
        logger.LogInformation(
            "Automatic marker added for {GameTitle}: {Reason}",
            snapshot.TargetTitle,
            decision.Reason);
    }
}
