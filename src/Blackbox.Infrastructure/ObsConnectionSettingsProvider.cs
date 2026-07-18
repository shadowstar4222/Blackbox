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
        Save(settings);
        Volatile.Write(ref _current, settings);
    }

    private void Save(ObsConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(_settingsPath))
        {
            return;
        }

        AtomicFileWriter.WriteAllText(
            _settingsPath,
            JsonSerializer.Serialize(settings, JsonOptions));
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
