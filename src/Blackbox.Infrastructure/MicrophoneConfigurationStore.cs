using System.Text.Json;
using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public sealed class MicrophoneConfigurationStore : IMicrophoneConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string? _settingsPath;
    private MicrophoneConfiguration _current;

    public MicrophoneConfigurationStore()
    {
        _current = new MicrophoneConfiguration();
    }

    public MicrophoneConfigurationStore(ObsProvisioningOptions options)
    {
        _settingsPath = options.MicrophoneSettingsPath;
        _current = Load(_settingsPath);
    }

    public MicrophoneConfiguration Current => Volatile.Read(ref _current);

    public void Save(MicrophoneConfiguration configuration)
    {
        Validate(configuration);
        Volatile.Write(ref _current, configuration);
        if (string.IsNullOrWhiteSpace(_settingsPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The microphone settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, JsonOptions));
        File.Move(temporaryPath, _settingsPath, true);
    }

    private static MicrophoneConfiguration Load(string? settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
        {
            return new MicrophoneConfiguration();
        }

        try
        {
            var configuration = JsonSerializer.Deserialize<MicrophoneConfiguration>(
                File.ReadAllText(settingsPath),
                JsonOptions) ?? new MicrophoneConfiguration();
            Validate(configuration);
            return configuration;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return new MicrophoneConfiguration();
        }
    }

    private static void Validate(MicrophoneConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.DeviceId))
        {
            throw new InvalidOperationException("A microphone device is required.");
        }

        configuration.ProcessingSettings.Validate();
    }
}
