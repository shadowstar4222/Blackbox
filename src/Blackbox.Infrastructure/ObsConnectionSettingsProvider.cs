using System.Text.Json;
using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public sealed class ObsConnectionSettingsProvider : IObsConnectionSettingsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string? _settingsPath;
    private ObsConnectionSettings _current;

    public ObsConnectionSettingsProvider()
    {
        _current = new ObsConnectionSettings();
    }

    public ObsConnectionSettingsProvider(ObsProvisioningOptions options)
    {
        _settingsPath = options.ConnectionSettingsPath;
        _current = Load(_settingsPath);
    }

    public ObsConnectionSettings Current => Volatile.Read(ref _current);

    public void Set(ObsConnectionSettings settings)
    {
        settings.Validate();
        Volatile.Write(ref _current, settings);
        Save(settings);
    }

    private void Save(ObsConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(_settingsPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The OBS connection settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, _settingsPath, true);
    }

    private static ObsConnectionSettings Load(string? settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
        {
            return new ObsConnectionSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<ObsConnectionSettings>(
                File.ReadAllText(settingsPath),
                JsonOptions) ?? new ObsConnectionSettings();
            settings.Validate();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return new ObsConnectionSettings();
        }
    }
}
