using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blackbox.Capture.Protocol;

public sealed class CaptureCommand
{
    public long Id { get; set; }
    public string Operation { get; set; } = "status";
    public CaptureStartRequest? Start { get; set; }
    public long WindowHandle { get; set; }
    public int ProcessId { get; set; }
    public string? MicrophoneId { get; set; }
    public bool Muted { get; set; }
    public double? MicrophoneGainDb { get; set; }
    public CaptureMicrophoneProcessing? MicrophoneProcessing { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeRuntimeStatistics { get; set; }
}

public sealed class CaptureStartRequest
{
    public long WindowHandle { get; set; }
    public int ProcessId { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string FilePrefix { get; set; } = "capture-";
    public string FfmpegPath { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int FramesPerSecond { get; set; } = 60;
    public int VideoBitrate { get; set; } = 12_000_000;
    public int AudioBitrate { get; set; } = 160_000;
    public int SegmentSeconds { get; set; } = 120;
    public bool CaptureSystemAudio { get; set; } = true;
    public bool CaptureMicrophone { get; set; } = true;
    public string? MicrophoneId { get; set; }
    public double MicrophoneGainDb { get; set; }
    public CaptureMicrophoneProcessing MicrophoneProcessing { get; set; } = new();
}

public sealed class CaptureMicrophoneProcessing
{
    public string Preset { get; set; } = "Broadcast";
    public bool Enabled { get; set; } = true;
    public bool HighPassEnabled { get; set; } = true;
    public double HighPassCutoffHz { get; set; } = 80;
    public bool NoiseSuppressionEnabled { get; set; } = true;
    public double NoiseSuppressionDb { get; set; } = -24;
    public bool ExpanderEnabled { get; set; } = true;
    public double ExpanderThresholdDb { get; set; } = -45;
    public double ExpanderRatio { get; set; } = 2.5;
    public double LowMidEqDb { get; set; } = -2;
    public double PresenceEqDb { get; set; } = 2;
    public bool CompressorEnabled { get; set; } = true;
    public double CompressorThresholdDb { get; set; } = -18;
    public double CompressorRatio { get; set; } = 3;
    public double CompressorMakeupGainDb { get; set; } = 2;
    public bool DeEsserEnabled { get; set; } = true;
    public double DeEsserThresholdDb { get; set; } = -20;
    public bool LimiterEnabled { get; set; } = true;
    public double LimiterThresholdDb { get; set; } = -1;
}

public sealed class CaptureReply
{
    public long Id { get; set; }
    public bool Success { get; set; } = true;
    public string? Error { get; set; }
    public string State { get; set; } = "idle";
    public long Frames { get; set; }
    public long BytesWritten { get; set; }
    public double DurationSeconds { get; set; }
    public string? OutputPath { get; set; }
    public string? Evidence { get; set; }
    public long WindowHandle { get; set; }
    public int ProcessId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool HardwareEncodingRequested { get; set; } = true;
    public bool Paused { get; set; }
    public string? Diagnostic { get; set; }
    public string? SnapshotPngBase64 { get; set; }
    public int SnapshotWidth { get; set; }
    public int SnapshotHeight { get; set; }
    public double? SnapshotMeanLuma { get; set; }
    public double? SnapshotBlackFraction { get; set; }
    public bool AudioCaptureEnabled { get; set; }
    public double? AudioPeakDb { get; set; }
    public long AudioPacketCount { get; set; }
    public bool MicrophoneCaptureEnabled { get; set; }
    public bool MicrophonePacketReceived { get; set; }
    public long MicrophonePacketCount { get; set; }
    public bool MicrophoneProcessingEnabled { get; set; }
    public string? MicrophonePreset { get; set; }
    public long MicrophoneDroppedFrameCount { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CaptureRuntimeStatistics? RuntimeStatistics { get; set; }
}

public sealed record CaptureRuntimeStatistics(long ManagedHeapBytes, long GcCommittedBytes,
    long GcHeapSizeBytes, long GcFragmentedBytes, long TotalAllocatedBytes,
    int Gen0Collections, int Gen1Collections, int Gen2Collections);

public static class CaptureJson
{
    public const int MaximumCommandLength = 65_536;

    // PascalCase output matches the DTOs; input also accepts camelCase clients.
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16
    };
}
