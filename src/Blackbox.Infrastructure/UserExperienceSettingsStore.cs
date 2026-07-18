using System.Text.Json;
using System.Text.Json.Serialization;
using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public sealed class UserExperienceSettingsStore : IRecordingQualitySettingsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsPath;
    private UserExperienceSettings _current;

    public UserExperienceSettingsStore(UserExperienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SettingsPath);
        _settingsPath = options.SettingsPath;
        _current = Load(_settingsPath);
    }

    public UserExperienceSettings Current => Volatile.Read(ref _current);
    RecordingQualitySettings IRecordingQualitySettingsProvider.Current =>
        Current.RecordingQuality ?? new RecordingQualitySettings();

    public void Save(UserExperienceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.RecordingQuality);
        settings.RecordingQuality.Validate();
        AtomicFileWriter.WriteAllText(
            _settingsPath,
            JsonSerializer.Serialize(settings, JsonOptions));
        Volatile.Write(ref _current, settings);
    }

    private static UserExperienceSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new UserExperienceSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<UserExperienceSettings>(
                File.ReadAllText(path),
                JsonOptions) ?? new UserExperienceSettings();
            var quality = settings.RecordingQuality ?? new RecordingQualitySettings();
            try
            {
                quality.Validate();
                return settings with { RecordingQuality = quality };
            }
            catch (InvalidOperationException)
            {
                return settings with { RecordingQuality = new RecordingQualitySettings() };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new UserExperienceSettings();
        }
    }
}
