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
        Volatile.Write(ref _wasEnabled, enabled ? 1 : 0);
        if (string.IsNullOrWhiteSpace(_settingsPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The automatic-capture settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new StoredPreference(enabled), JsonOptions));
        File.Move(temporaryPath, _settingsPath, true);
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
