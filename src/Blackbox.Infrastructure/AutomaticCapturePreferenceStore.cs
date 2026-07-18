using System.Text.Json;
using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public sealed class AutomaticCapturePreferenceStore : IAutomaticCapturePreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string? _settingsPath;
    private int _wasEnabled;

    public AutomaticCapturePreferenceStore()
    {
    }

    public AutomaticCapturePreferenceStore(ObsProvisioningOptions options)
    {
        _settingsPath = options.AutomaticCaptureSettingsPath;
        _wasEnabled = Load(_settingsPath) ? 1 : 0;
    }

    public bool WasEnabled => Volatile.Read(ref _wasEnabled) == 1;

    public void Save(bool enabled)
    {
        if (!string.IsNullOrWhiteSpace(_settingsPath))
        {
            AtomicFileWriter.WriteAllText(
                _settingsPath,
                JsonSerializer.Serialize(new StoredPreference(enabled), JsonOptions));
        }

        Volatile.Write(ref _wasEnabled, enabled ? 1 : 0);
    }

    private static bool Load(string? settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
        {
            return false;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredPreference>(
                File.ReadAllText(settingsPath),
                JsonOptions)?.Enabled ?? false;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return false;
        }
    }

    private sealed record StoredPreference(bool Enabled);
}
