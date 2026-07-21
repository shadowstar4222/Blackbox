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
        if (!string.IsNullOrWhiteSpace(_settingsPath))
        {
            AtomicFileWriter.WriteAllText(
                _settingsPath,
                JsonSerializer.Serialize(configuration, JsonOptions));
        }

        Volatile.Write(ref _current, configuration);
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

        if (configuration.ExcludedDeviceIds is null ||
            configuration.ExcludedDeviceIds.Any(string.IsNullOrWhiteSpace) ||
            configuration.ExcludedDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            configuration.ExcludedDeviceIds.Count)
        {
            throw new InvalidOperationException("Microphone exclusions must contain unique device identifiers.");
        }

        configuration.ProcessingSettings.Validate();
    }
}
