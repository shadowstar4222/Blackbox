namespace Blackbox.Domain;

public enum RecordingEncoderPreference
{
    Automatic,
    Hardware,
    Software
}

public enum RecordingQualityPreset
{
    Performance,
    Balanced,
    Quality,
    Maximum
}

public enum GameExportContainer
{
    Mkv,
    Mp4
}

public sealed record CaptureCrop
{
    public int LeftPercent { get; init; }
    public int TopPercent { get; init; }
    public int RightPercent { get; init; }
    public int BottomPercent { get; init; }

    public void Validate()
    {
        if (LeftPercent is < 0 or > 45 ||
            TopPercent is < 0 or > 45 ||
            RightPercent is < 0 or > 45 ||
            BottomPercent is < 0 or > 45 ||
            LeftPercent + RightPercent >= 90 ||
            TopPercent + BottomPercent >= 90)
        {
            throw new InvalidOperationException("Capture crop percentages must leave a visible center area.");
        }
    }
}

public interface IGameCaptureTransformSettingsProvider
{
    CaptureCrop? CurrentCrop { get; }
}

public sealed record GameExportPreset
{
    public GameExportContainer Container { get; init; } = GameExportContainer.Mkv;
    public bool IncludeSeparateAudioTracks { get; init; }
    public bool ReencodeVideo { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(Container))
        {
            throw new InvalidOperationException("The game export container is invalid.");
        }
    }
}

public sealed record GameNotificationPreferences
{
    public bool RoutineEvents { get; init; } = true;
    public bool CaptureFailures { get; init; } = true;
    public bool DeviceChanges { get; init; } = true;
}

public sealed record GameProfileOverrides
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public RecordingResolution? Resolution { get; init; }
    public int? FramesPerSecond { get; init; }
    public int? AudioBitrateKbps { get; init; }
    public RecordingEncoderPreference? EncoderPreference { get; init; }
    public RecordingQualityPreset? QualityPreset { get; init; }
    public int? SegmentDurationMinutes { get; init; }
    public decimal? MaximumStorageGigabytes { get; init; }
    public int? MaximumRetentionHours { get; init; }
    public int? CaptureDelaySeconds { get; init; }
    public bool? CaptureVoiceChat { get; init; }
    public bool? CaptureMicrophone { get; init; }
    public MicrophoneProcessingSettings? MicrophoneProcessing { get; init; }
    public CaptureCrop? Crop { get; init; }
    public GameExportPreset? ExportPreset { get; init; }
    public GameNotificationPreferences? Notifications { get; init; }
    public GameInputControls? InputControls { get; init; }
    public AutomaticMarkerSettings? AutomaticMarkers { get; init; }

    public bool HasOverrides =>
        Resolution is not null ||
        FramesPerSecond is not null ||
        AudioBitrateKbps is not null ||
        EncoderPreference is not null ||
        QualityPreset is not null ||
        SegmentDurationMinutes is not null ||
        MaximumStorageGigabytes is not null ||
        MaximumRetentionHours is not null ||
        CaptureDelaySeconds is not null ||
        CaptureVoiceChat is not null ||
        CaptureMicrophone is not null ||
        MicrophoneProcessing is not null ||
        Crop is not null ||
        ExportPreset is not null ||
        Notifications is not null ||
        InputControls is not null ||
        AutomaticMarkers is not null;

    public void Validate()
    {
        if (SchemaVersion is < 1 or > CurrentSchemaVersion)
        {
            throw new InvalidOperationException("The game profile override schema is not supported.");
        }

        if (Resolution is not null && !Enum.IsDefined(Resolution.Value))
        {
            throw new InvalidOperationException("The game recording resolution override is invalid.");
        }

        if (FramesPerSecond is not null and not (30 or 60 or 120))
        {
            throw new InvalidOperationException("The game frame-rate override must be 30, 60, or 120 FPS.");
        }

        if (AudioBitrateKbps is not null and not (160 or 192 or 256 or 320))
        {
            throw new InvalidOperationException("The game audio override must be 160 or 192 kbps (legacy 256/320 settings are accepted for migration).");
        }

        if (EncoderPreference is not null && !Enum.IsDefined(EncoderPreference.Value))
        {
            throw new InvalidOperationException("The game encoder preference is invalid.");
        }

        if (QualityPreset is not null && !Enum.IsDefined(QualityPreset.Value))
        {
            throw new InvalidOperationException("The game quality preset is invalid.");
        }

        if (SegmentDurationMinutes is not null and (< 1 or > 10))
        {
            throw new InvalidOperationException("The game segment duration must be between 1 and 10 minutes.");
        }

        if (MaximumStorageGigabytes is <= 0)
        {
            throw new InvalidOperationException("The game storage override must be greater than zero.");
        }

        if (MaximumRetentionHours is not null and (< 1 or > 24 * 365))
        {
            throw new InvalidOperationException("The game retention override must be between 1 hour and 1 year.");
        }

        if (CaptureDelaySeconds is not null and (< 0 or > 60))
        {
            throw new InvalidOperationException("The game capture delay must be between 0 and 60 seconds.");
        }

        MicrophoneProcessing?.Validate();
        Crop?.Validate();
        ExportPreset?.Validate();
        InputControls?.Validate();
        AutomaticMarkers?.Validate();
    }
}

public sealed record GameProfileDefaults(
    RecordingQualitySettings RecordingQuality,
    RecordingSettings Recording,
    MicrophoneProcessingSettings MicrophoneProcessing,
    RecordingEncoderPreference EncoderPreference = RecordingEncoderPreference.Automatic,
    RecordingQualityPreset QualityPreset = RecordingQualityPreset.Balanced,
    TimeSpan? CaptureDelay = null,
    bool CaptureVoiceChat = true,
    bool CaptureMicrophone = true,
    GameExportPreset? ExportPreset = null,
    GameNotificationPreferences? Notifications = null,
    GameInputControls? InputControls = null,
    AutomaticMarkerSettings? AutomaticMarkers = null)
{
    public void Validate()
    {
        RecordingQuality.Validate();
        Recording.Validate();
        MicrophoneProcessing.Validate();
        ExportPreset?.Validate();
        InputControls?.Validate();
        AutomaticMarkers?.Validate();
    }
}

public sealed record EffectiveGameProfileSettings(
    string ProfileName,
    RecordingQualitySettings RecordingQuality,
    RecordingSettings Recording,
    MicrophoneProcessingSettings MicrophoneProcessing,
    RecordingEncoderPreference EncoderPreference,
    RecordingQualityPreset QualityPreset,
    TimeSpan CaptureDelay,
    bool CaptureVoiceChat,
    bool CaptureMicrophone,
    CaptureCrop? Crop,
    GameExportPreset ExportPreset,
    GameNotificationPreferences Notifications,
    GameInputControls InputControls,
    AutomaticMarkerSettings AutomaticMarkers,
    IReadOnlySet<string> OverriddenFields)
{
    public bool IsInherited(string fieldName) => !OverriddenFields.Contains(fieldName);
}

public static class GameProfileSettingsResolver
{
    public static EffectiveGameProfileSettings Resolve(
        GameProfile? profile,
        GameProfileDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        defaults.Validate();
        profile?.Validate();
        var overrides = profile?.Overrides ?? new GameProfileOverrides();
        overrides.Validate();
        var fields = new HashSet<string>(StringComparer.Ordinal);
        AddIfOverridden(fields, nameof(overrides.Resolution), overrides.Resolution);
        AddIfOverridden(fields, nameof(overrides.FramesPerSecond), overrides.FramesPerSecond);
        AddIfOverridden(fields, nameof(overrides.AudioBitrateKbps), overrides.AudioBitrateKbps);
        AddIfOverridden(fields, nameof(overrides.EncoderPreference), overrides.EncoderPreference);
        AddIfOverridden(fields, nameof(overrides.QualityPreset), overrides.QualityPreset);
        AddIfOverridden(fields, nameof(overrides.SegmentDurationMinutes), overrides.SegmentDurationMinutes);
        AddIfOverridden(fields, nameof(overrides.MaximumStorageGigabytes), overrides.MaximumStorageGigabytes);
        AddIfOverridden(fields, nameof(overrides.MaximumRetentionHours), overrides.MaximumRetentionHours);
        AddIfOverridden(fields, nameof(overrides.CaptureDelaySeconds), overrides.CaptureDelaySeconds);
        AddIfOverridden(fields, nameof(overrides.CaptureVoiceChat), overrides.CaptureVoiceChat);
        AddIfOverridden(fields, nameof(overrides.CaptureMicrophone), overrides.CaptureMicrophone);
        AddIfOverridden(fields, nameof(overrides.MicrophoneProcessing), overrides.MicrophoneProcessing);
        AddIfOverridden(fields, nameof(overrides.Crop), overrides.Crop);
        AddIfOverridden(fields, nameof(overrides.ExportPreset), overrides.ExportPreset);
        AddIfOverridden(fields, nameof(overrides.Notifications), overrides.Notifications);
        AddIfOverridden(fields, nameof(overrides.InputControls), overrides.InputControls);
        AddIfOverridden(fields, nameof(overrides.AutomaticMarkers), overrides.AutomaticMarkers);

        return new EffectiveGameProfileSettings(
            profile?.DisplayName ?? "Global defaults",
            defaults.RecordingQuality with
            {
                Resolution = overrides.Resolution ?? defaults.RecordingQuality.Resolution,
                FramesPerSecond = overrides.FramesPerSecond ?? defaults.RecordingQuality.FramesPerSecond,
                AudioBitrateKbps = overrides.AudioBitrateKbps ?? defaults.RecordingQuality.AudioBitrateKbps
            },
            defaults.Recording with
            {
                SegmentDurationMinutes =
                    overrides.SegmentDurationMinutes ?? defaults.Recording.SegmentDurationMinutes,
                MaximumStorageGigabytes =
                    overrides.MaximumStorageGigabytes ?? defaults.Recording.MaximumStorageGigabytes,
                MaximumRetainedDuration = overrides.MaximumRetentionHours is null
                    ? defaults.Recording.MaximumRetainedDuration
                    : TimeSpan.FromHours(overrides.MaximumRetentionHours.Value)
            },
            overrides.MicrophoneProcessing ?? defaults.MicrophoneProcessing,
            overrides.EncoderPreference ?? defaults.EncoderPreference,
            overrides.QualityPreset ?? defaults.QualityPreset,
            overrides.CaptureDelaySeconds is null
                ? defaults.CaptureDelay ?? TimeSpan.Zero
                : TimeSpan.FromSeconds(overrides.CaptureDelaySeconds.Value),
            overrides.CaptureVoiceChat ?? defaults.CaptureVoiceChat,
            overrides.CaptureMicrophone ?? defaults.CaptureMicrophone,
            overrides.Crop,
            overrides.ExportPreset ?? defaults.ExportPreset ?? new GameExportPreset(),
            overrides.Notifications ?? defaults.Notifications ?? new GameNotificationPreferences(),
            overrides.InputControls ?? defaults.InputControls ?? new GameInputControls(),
            overrides.AutomaticMarkers ?? defaults.AutomaticMarkers ?? new AutomaticMarkerSettings(),
            fields);
    }

    private static void AddIfOverridden<T>(
        ISet<string> fields,
        string fieldName,
        T? value)
        where T : struct
    {
        if (value is not null)
        {
            fields.Add(fieldName);
        }
    }

    private static void AddIfOverridden(
        ISet<string> fields,
        string fieldName,
        object? value)
    {
        if (value is not null)
        {
            fields.Add(fieldName);
        }
    }
}
