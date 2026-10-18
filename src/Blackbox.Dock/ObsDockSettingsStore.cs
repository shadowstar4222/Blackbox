using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Blackbox.Dock;

public sealed record ObsDockSettings(int Version, int Port, string Token);

public sealed class ObsDockSettingsStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly object _syncRoot = new();
    private readonly ObsDockOptions _options;
    private readonly ILogger<ObsDockSettingsStore> _logger;
    private ObsDockSettings? _current;

    public ObsDockSettingsStore(
        ObsDockOptions options,
        ILogger<ObsDockSettingsStore> logger)
    {
        options.Validate();
        _options = options;
        _logger = logger;
    }

    public ObsDockSettings LoadOrCreate()
    {
        lock (_syncRoot)
        {
            if (_current is not null)
            {
                return _current;
            }

            if (File.Exists(_options.SettingsPath))
            {
                try
                {
                    var stored = JsonSerializer.Deserialize<ObsDockSettings>(
                        File.ReadAllText(_options.SettingsPath),
                        JsonOptions);
                    if (IsValid(stored))
                    {
                        _current = stored;
                        return stored!;
                    }

                    _logger.LogWarning("The OBS dock settings were invalid and will be regenerated.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _logger.LogWarning(ex, "The OBS dock settings could not be read and will be regenerated.");
                }
            }

            _current = new ObsDockSettings(
                CurrentVersion,
                0,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
            Write(_current);
            return _current;
        }
    }

    public ObsDockSettings UpdatePort(int port)
    {
        if (port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "The OBS dock port is outside the allowed range.");
        }

        lock (_syncRoot)
        {
            var current = _current ?? LoadOrCreate();
            if (current.Port == port)
            {
                return current;
            }

            _current = current with { Port = port };
            Write(_current);
            return _current;
        }
    }

    public Uri GetDockUri()
    {
        var settings = LoadOrCreate();
        if (settings.Port == 0)
        {
            throw new InvalidOperationException("The OBS dock server has not selected a local port yet.");
        }

        return new Uri($"http://127.0.0.1:{settings.Port}/dock/{settings.Token}/");
    }

    private static bool IsValid(ObsDockSettings? settings) =>
        settings is not null &&
        settings.Version == CurrentVersion &&
        settings.Port is >= 0 and <= 65535 &&
        settings.Token.Length == 64 &&
        settings.Token.All(Uri.IsHexDigit);

    private void Write(ObsDockSettings settings)
    {
        var directory = Path.GetDirectoryName(_options.SettingsPath)
            ?? throw new InvalidOperationException("The OBS dock settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_options.SettingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, _options.SettingsPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
