using System.Text.Json;
using System.Text.Json.Serialization;
using Blackbox.Domain;
using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Blackbox.Recording;

public sealed class RecordingQualityContext(
    UserExperienceSettingsStore globalSettings) :
    IRecordingQualitySettingsProvider,
    IGameCaptureTransformSettingsProvider
{
    private RecordingQualitySettings? _active;
    private CaptureCrop? _activeCrop;

    public RecordingQualitySettings Current =>
        Volatile.Read(ref _active) ??
        globalSettings.Current.RecordingQuality ??
        new RecordingQualitySettings();
    public CaptureCrop? CurrentCrop => Volatile.Read(ref _activeCrop);

    public void Activate(RecordingQualitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Volatile.Write(ref _active, settings);
        Volatile.Write(ref _activeCrop, null);
    }

    public void Activate(EffectiveGameProfileSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.RecordingQuality.Validate();
        settings.Crop?.Validate();
        Volatile.Write(ref _active, settings.RecordingQuality);
        Volatile.Write(ref _activeCrop, settings.Crop);
    }

    public void Clear()
    {
        Volatile.Write(ref _active, null);
        Volatile.Write(ref _activeCrop, null);
    }
}

public sealed class GameProfileSettingsService(
    IGameProfileRepository profileRepository,
    UserExperienceSettingsStore experienceSettings,
    MicrophoneConfigurationStore microphoneConfiguration,
    RecordingSettings recordingSettings,
    AutomaticCaptureOptions automaticCaptureOptions,
    IClock clock,
    ILogger<GameProfileSettingsService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public EffectiveGameProfileSettings GlobalDefaults =>
        GameProfileSettingsResolver.Resolve(null, BuildDefaults());

    public async Task<EffectiveGameProfileSettings> ResolveAsync(
        GameCaptureTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var profiles = await profileRepository.GetAllAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(candidate =>
            candidate.MatchesExecutablePath(target.ExecutablePath));
        return GameProfileSettingsResolver.Resolve(profile, BuildDefaults());
    }

    public EffectiveGameProfileSettings Resolve(GameProfile profile) =>
        GameProfileSettingsResolver.Resolve(profile, BuildDefaults());

    public void WriteSessionMetadata(
        string sessionDirectory,
        GameCaptureTarget? target,
        EffectiveGameProfileSettings effective)
    {
        try
        {
            Directory.CreateDirectory(sessionDirectory);
            var destinationPath = Path.Combine(sessionDirectory, "blackbox-session.json");
            var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                var metadata = new AppliedGameProfileMetadata(
                    1,
                    effective.ProfileName,
                    target?.ExecutablePath,
                    target?.ObsWindowIdentifier,
                    clock.UtcNow,
                    effective);
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(metadata, JsonOptions));
                File.Move(temporaryPath, destinationPath, true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write applied game-profile metadata for {ProfileName}.", effective.ProfileName);
        }
    }

    private GameProfileDefaults BuildDefaults() => new(
        experienceSettings.Current.RecordingQuality ?? new RecordingQualitySettings(),
        recordingSettings,
        microphoneConfiguration.Current.ProcessingSettings,
        CaptureDelay: automaticCaptureOptions.CaptureSettleDelay);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed record AppliedGameProfileMetadata(
    int SchemaVersion,
    string ProfileName,
    string? ExecutablePath,
    string? ObsWindowIdentifier,
    DateTimeOffset AppliedAt,
    EffectiveGameProfileSettings EffectiveSettings);
