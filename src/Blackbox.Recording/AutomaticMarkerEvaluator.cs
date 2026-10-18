using System.Globalization;
using Blackbox.Domain;

namespace Blackbox.Recording;

public sealed class AutomaticMarkerEvaluator
{
    public AutomaticMarkerDecision? Evaluate(
        AutomaticMarkerSignal signal,
        AutomaticMarkerSignal? previous,
        DateTimeOffset? lastMarkerAt,
        AutomaticMarkerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (!settings.Enabled ||
            lastMarkerAt is not null &&
            signal.Timestamp - lastMarkerAt.Value < settings.Cooldown)
        {
            return null;
        }

        if (settings.DetectInputRules && !string.IsNullOrWhiteSpace(signal.InputRule))
        {
            return new AutomaticMarkerDecision(
                TimelineMarkerOrigin.InputRule,
                $"Input: {signal.InputRule.Trim()}",
                $"The configured input rule '{signal.InputRule.Trim()}' fired.");
        }

        if (settings.DetectAudioSpikes &&
            signal.AudioPeakDb is not null &&
            signal.AudioPeakDb.Value >= settings.AudioSpikeThresholdDb)
        {
            return new AutomaticMarkerDecision(
                TimelineMarkerOrigin.AudioSpike,
                "Audio spike",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Game audio peaked at {signal.AudioPeakDb.Value:0.0} dB, above the {settings.AudioSpikeThresholdDb:0.0} dB threshold."));
        }

        if (settings.DetectSceneChanges &&
            signal.MeanLuminance is not null &&
            previous?.MeanLuminance is not null)
        {
            var change = Math.Abs(signal.MeanLuminance.Value - previous.MeanLuminance.Value);
            if (change >= settings.SceneLuminanceDeltaThreshold)
            {
                return new AutomaticMarkerDecision(
                    TimelineMarkerOrigin.SceneChange,
                    "Scene change",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Preview luminance changed by {change:P0}, above the {settings.SceneLuminanceDeltaThreshold:P0} threshold."));
            }
        }

        return null;
    }
}
