using System.Text.Json;
using Blackbox.Domain;

namespace Blackbox.Infrastructure;

public sealed class GameCaptureSelectionStore : IGameCaptureSelectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly string? _settingsPath;
    private GameCaptureSelection? _current;

    public GameCaptureSelectionStore()
    {
    }

    public GameCaptureSelectionStore(ObsProvisioningOptions options)
    {
        _settingsPath = options.GameCaptureSelectionSettingsPath;
        _current = Load(_settingsPath);
    }

    public GameCaptureSelection? Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public void Save(GameCaptureSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        selection.Validate();
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(_settingsPath))
            {
                AtomicFileWriter.WriteAllText(
                    _settingsPath,
                    JsonSerializer.Serialize(selection, JsonOptions));
            }

            _current = selection;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(_settingsPath) && File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
            }

            _current = null;
        }
    }

    private static GameCaptureSelection? Load(string? settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
        {
            return null;
        }

        try
        {
            var selection = JsonSerializer.Deserialize<GameCaptureSelection>(
                File.ReadAllText(settingsPath),
                JsonOptions);
            selection?.Validate();
            return selection;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
