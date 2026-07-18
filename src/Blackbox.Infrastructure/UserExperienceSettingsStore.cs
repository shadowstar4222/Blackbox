using System.Text.Json;

namespace Blackbox.Infrastructure;

public sealed class UserExperienceSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
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

    public void Save(UserExperienceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
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
            return JsonSerializer.Deserialize<UserExperienceSettings>(
                File.ReadAllText(path),
                JsonOptions) ?? new UserExperienceSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new UserExperienceSettings();
        }
    }
}
