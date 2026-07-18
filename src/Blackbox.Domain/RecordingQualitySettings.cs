namespace Blackbox.Domain;

public enum RecordingResolution
{
    MatchApplication,
    Hd720,
    FullHd1080,
    QuadHd1440,
    UltraHd2160
}

public sealed record RecordingQualitySettings
{
    public RecordingResolution Resolution { get; init; } = RecordingResolution.FullHd1080;
    public int FramesPerSecond { get; init; } = 60;
    public int AudioBitrateKbps { get; init; } = 256;

    public void Validate()
    {
        if (!Enum.IsDefined(Resolution))
        {
            throw new InvalidOperationException("Recording resolution is invalid.");
        }

        if (FramesPerSecond is not (30 or 60 or 120))
        {
            throw new InvalidOperationException("Recording frame rate must be 30, 60, or 120 FPS.");
        }

        if (AudioBitrateKbps is not (160 or 256 or 320))
        {
            throw new InvalidOperationException("Recording audio quality must be 160, 256, or 320 kbps.");
        }
    }
}

public interface IRecordingQualitySettingsProvider
{
    RecordingQualitySettings Current { get; }
}
