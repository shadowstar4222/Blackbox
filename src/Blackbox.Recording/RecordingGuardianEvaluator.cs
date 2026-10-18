using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.Recording;

public sealed class RecordingGuardianEvaluator(RecordingGuardianOptions options)
{
    private int _blackSamples;
    private int _frozenSamples;
    private int _silentSamples;
    private int _missingPreviewSamples;
    private int _unavailableSamples;
    private int _healthySamples;
    private string? _lastFingerprint;
    private RecordingFrameAnalysis? _lastPreviewFrame;

    public RecordingGuardianDecision Evaluate(
        ObsCaptureHealthSample sample,
        RecordingFrameAnalysis? frame,
        GameCaptureTarget? target,
        bool blackboxIsRecording)
    {
        options.Validate();
        _unavailableSamples = 0;
        var issue = RecordingGuardianIssue.None;
        var evidence = new List<string>();
        var repairAction = RecordingGuardianRepairAction.None;
        var isFault = false;
        if (!sample.SupportsPreview) _lastPreviewFrame = null;
        else if (sample.PreviewSampled) _lastPreviewFrame = frame;
        else frame = _lastPreviewFrame;

        if (!sample.SourceExists)
        {
            issue |= RecordingGuardianIssue.MissingSource;
            evidence.Add(sample.Evidence ?? "The selected capture window could not be found.");
            isFault = true;
        }
        else
        {
            if (!sample.SourceActive)
            {
                issue |= RecordingGuardianIssue.InactiveSource;
                evidence.Add(sample.Evidence ?? "The selected capture window is minimized or unavailable.");
                repairAction = RecordingGuardianRepairAction.RefreshCapture;
            }

            if (!sample.SourceMatchesTarget)
            {
                issue |= RecordingGuardianIssue.WrongWindow;
                evidence.Add(
                    $"Capture is bound to '{sample.BoundWindowIdentifier ?? "nothing"}' instead of the selected window.");
                repairAction = RecordingGuardianRepairAction.RefreshCapture;
            }
        }

        if (blackboxIsRecording && !sample.RecordingOutputActive)
        {
            issue |= RecordingGuardianIssue.RecordingStopped;
            evidence.Add("Blackbox owns a recording session but the recording output is stopped.");
            repairAction = RecordingGuardianRepairAction.RestartRecordingOutput;
        }

        var isBlack = frame is not null &&
            frame.MeanLuminance <= options.BlackMeanLuminanceThreshold &&
            frame.DarkPixelRatio >= options.BlackDarkPixelRatioThreshold;
        if (sample.PreviewSampled) _blackSamples = isBlack ? _blackSamples + 1 : 0;
        if (isBlack && _blackSamples >= options.BlackFrameConfirmationSamples)
        {
            issue |= RecordingGuardianIssue.BlackFrame;
            evidence.Add(
                $"Preview remained black for {_blackSamples} checks " +
                $"(mean {frame!.MeanLuminance:P1}, dark {frame.DarkPixelRatio:P1}).");
            if (target is not null &&
                repairAction != RecordingGuardianRepairAction.RestartRecordingOutput)
            {
                repairAction = RecordingGuardianRepairAction.RefreshCapture;
            }
        }

        if (frame is null && sample.SupportsPreview && sample.PreviewSampled)
        {
            _missingPreviewSamples++;
            _frozenSamples = 0;
            _lastFingerprint = null;
        }
        else if (frame is not null && sample.PreviewSampled)
        {
            _missingPreviewSamples = 0;
            if (!isBlack &&
                blackboxIsRecording &&
                string.Equals(frame.Fingerprint, _lastFingerprint, StringComparison.Ordinal))
            {
                _frozenSamples++;
            }
            else
            {
                _frozenSamples = 0;
            }

            _lastFingerprint = frame.Fingerprint;
        }
        else if (!sample.SupportsPreview)
        {
            _missingPreviewSamples = _frozenSamples = 0;
            _lastFingerprint = null;
        }

        if (_missingPreviewSamples >= options.MissingPreviewConfirmationSamples)
        {
            issue |= RecordingGuardianIssue.PreviewUnavailable;
            evidence.Add($"The recorder returned no preview for {_missingPreviewSamples} checks.");
        }

        if (_frozenSamples >= options.FrozenFrameConfirmationSamples)
        {
            issue |= RecordingGuardianIssue.FrozenFrame;
            evidence.Add($"The capture image did not change for {_frozenSamples + 1} checks.");
            if (target is not null &&
                repairAction != RecordingGuardianRepairAction.RestartRecordingOutput)
            {
                repairAction = RecordingGuardianRepairAction.RefreshCapture;
            }
        }

        var expectsGameAudio = sample.SupportsAudioMeter && blackboxIsRecording && target?.CaptureGameAudio == true;
        var isSilent = expectsGameAudio &&
            (sample.GameAudioSampleCount == 0 ||
             sample.GameAudioPeakDb is null ||
             sample.GameAudioPeakDb <= options.SilentAudioPeakDbThreshold);
        _silentSamples = isSilent ? _silentSamples + 1 : 0;
        if (_silentSamples >= options.SilentAudioConfirmationSamples)
        {
            issue |= RecordingGuardianIssue.SilentGameAudio;
            evidence.Add(sample.GameAudioSampleCount == 0
                ? $"The recorder returned no game-audio meter samples for {_silentSamples} checks."
                : $"Game audio stayed below {options.SilentAudioPeakDbThreshold:0} dB for {_silentSamples} checks.");
            if (target is not null &&
                repairAction != RecordingGuardianRepairAction.RestartRecordingOutput)
            {
                repairAction = RecordingGuardianRepairAction.RefreshCapture;
            }
        }

        if (issue == RecordingGuardianIssue.None)
        {
            _healthySamples++;
            return new RecordingGuardianDecision(
                issue,
                blackboxIsRecording
                    ? "Capture and recording output are healthy."
                    : target is null
                        ? "The recorder is ready; waiting for a selected window."
                        : "The selected window is visible and ready.",
                BuildHealthyEvidence(sample, frame),
                RecordingGuardianRepairAction.None,
                false,
                _healthySamples);
        }

        if (repairAction == RecordingGuardianRepairAction.RefreshCapture && !sample.SupportsAutomaticCaptureRefresh)
        {
            repairAction = RecordingGuardianRepairAction.None;
            evidence.Add("Automatic capture reset is unavailable for this backend. Restore the window; if the warning persists, stop and restart recording.");
        }
        _healthySamples = 0;
        return new RecordingGuardianDecision(
            issue,
            Describe(issue),
            string.Join(" ", evidence),
            isFault ? RecordingGuardianRepairAction.None : repairAction,
            isFault,
            0);
    }

    public RecordingGuardianDecision EvaluateUnavailable(Exception exception)
    {
        _unavailableSamples++;
        _healthySamples = 0;
        var confirmed = _unavailableSamples >= options.ObsUnavailableConfirmationSamples;
        return new RecordingGuardianDecision(
            RecordingGuardianIssue.ObsUnavailable,
            confirmed
                ? "Recording Guardian cannot reach the recorder."
                : "The recorder health check was interrupted; confirming.",
            exception.Message,
            RecordingGuardianRepairAction.None,
            confirmed,
            0);
    }

    public void Reset()
    {
        _blackSamples = 0;
        _frozenSamples = 0;
        _silentSamples = 0;
        _missingPreviewSamples = 0;
        _unavailableSamples = 0;
        _healthySamples = 0;
        _lastFingerprint = null;
        _lastPreviewFrame = null;
    }

    private static string BuildHealthyEvidence(
        ObsCaptureHealthSample sample,
        RecordingFrameAnalysis? frame)
    {
        var frameEvidence = frame is null
            ? "Preview unavailable."
            : $"Preview {frame.Width}x{frame.Height}, mean luminance {frame.MeanLuminance:P1}.";
        var audioEvidence = sample.GameAudioSampleCount == 0
            ? "No active game-audio samples."
            : $"Game-audio peak {sample.GameAudioPeakDb:0.0} dB.";
        var microphoneEvidence = sample.MicrophoneCaptureEnabled
            ? sample.MicrophonePacketReceived
                ? $"Microphone active ({sample.MicrophonePacketCount} packets verified)."
                : "Microphone enabled, but no packets have arrived."
            : "Microphone disabled by the active recording profile.";
        return $"{frameEvidence} Output {sample.OutputWidth}x{sample.OutputHeight} at {sample.FramesPerSecond:0.##} FPS. {audioEvidence} {microphoneEvidence}";
    }

    private static string Describe(RecordingGuardianIssue issue)
    {
        if (issue.HasFlag(RecordingGuardianIssue.MissingSource))
        {
            return "The selected capture window is unavailable. Select an open window in Games.";
        }

        if (issue.HasFlag(RecordingGuardianIssue.RecordingStopped))
        {
            return "The recorder stopped unexpectedly.";
        }

        if (issue.HasFlag(RecordingGuardianIssue.WrongWindow))
        {
            return "Capture is bound to the wrong window.";
        }

        if (issue.HasFlag(RecordingGuardianIssue.InactiveSource))
        {
            return "The game capture source is hidden or inactive.";
        }

        if (issue.HasFlag(RecordingGuardianIssue.BlackFrame))
        {
            return "The game capture preview is black.";
        }

        if (issue.HasFlag(RecordingGuardianIssue.FrozenFrame))
        {
            return "The game capture preview appears frozen.";
        }

        if (issue.HasFlag(RecordingGuardianIssue.SilentGameAudio))
        {
            return "Game audio appears unexpectedly silent.";
        }

        return "The live capture preview is unavailable.";
    }
}
