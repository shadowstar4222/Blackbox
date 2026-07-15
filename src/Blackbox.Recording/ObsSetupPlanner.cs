using Blackbox.Domain;

namespace Blackbox.Recording;

public sealed class ObsSetupPlanner
{
    public ObsSetupPlan CreateDefaultPlan(RecordingSettings recordingSettings)
    {
        recordingSettings.Validate();
        var microphoneSettings = new MicrophoneProcessingSettings();
        var audioRouting = AudioRoutingProfile.Default;

        return new ObsSetupPlan
        {
            RecordingDirectory = recordingSettings.RecordingLocation,
            SegmentMinutes = recordingSettings.SegmentDurationMinutes,
            AudioRoutingProfile = audioRouting,
            MicrophoneProcessingSettings = microphoneSettings,
            Sources =
            [
                new ObsSourcePlan("Blackbox Game Capture", "game_capture", AudioCategory.Game, new Dictionary<string, string>()),
                new ObsSourcePlan("Blackbox Game Audio", "wasapi_process_output_capture", AudioCategory.Game, new Dictionary<string, string>
                {
                    ["executable"] = "{game_executable}"
                }),
                new ObsSourcePlan("Blackbox Voice Chat", "wasapi_process_output_capture", AudioCategory.VoiceChat, new Dictionary<string, string>
                {
                    ["executable"] = "Discord.exe"
                }),
                new ObsSourcePlan("Blackbox Raw Microphone", "wasapi_input_capture", AudioCategory.RawMicrophone, new Dictionary<string, string>()),
                new ObsSourcePlan("Blackbox Processed Microphone", "wasapi_input_capture", AudioCategory.ProcessedMicrophone, new Dictionary<string, string>())
            ],
            Filters =
            [
                new ObsFilterPlan("Blackbox Processed Microphone", "Blackbox Noise Suppression", "noise_suppress_filter_v2", new Dictionary<string, string>()),
                new ObsFilterPlan("Blackbox Processed Microphone", "Blackbox Expander", "expander_filter", new Dictionary<string, string>
                {
                    ["threshold_db"] = microphoneSettings.ExpanderThresholdDb.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }),
                new ObsFilterPlan("Blackbox Processed Microphone", "Blackbox Compressor", "compressor_filter", new Dictionary<string, string>
                {
                    ["threshold_db"] = microphoneSettings.CompressorThresholdDb.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["ratio"] = microphoneSettings.CompressorRatio.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }),
                new ObsFilterPlan("Blackbox Processed Microphone", "Blackbox Limiter", "limiter_filter", new Dictionary<string, string>
                {
                    ["threshold_db"] = microphoneSettings.LimiterThresholdDb.ToString(System.Globalization.CultureInfo.InvariantCulture)
                })
            ]
        };
    }
}
