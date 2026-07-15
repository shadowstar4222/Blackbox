using System.Text.Json;
using System.Text.Json.Serialization;
using Blackbox.Domain;

namespace Blackbox.Infrastructure;

internal static class ObsWebSocketConfigurationWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static void Write(string obsRootDirectory, ObsConnectionSettings settings)
    {
        settings.Validate();
        var configurationDirectory = Path.Combine(
            obsRootDirectory,
            "config",
            "obs-studio",
            "plugin_config",
            "obs-websocket");
        Directory.CreateDirectory(configurationDirectory);
        var configurationPath = Path.Combine(configurationDirectory, "config.json");
        var configuration = new WebSocketConfiguration(
            AlertsEnabled: false,
            AuthenticationRequired: settings.UseAuthentication,
            FirstLoad: false,
            ServerEnabled: true,
            ServerPassword: settings.Password ?? string.Empty,
            ServerPort: settings.Port);
        File.WriteAllText(configurationPath, JsonSerializer.Serialize(configuration, JsonOptions));
    }

    private sealed record WebSocketConfiguration(
        [property: JsonPropertyName("alerts_enabled")] bool AlertsEnabled,
        [property: JsonPropertyName("auth_required")] bool AuthenticationRequired,
        [property: JsonPropertyName("first_load")] bool FirstLoad,
        [property: JsonPropertyName("server_enabled")] bool ServerEnabled,
        [property: JsonPropertyName("server_password")] string ServerPassword,
        [property: JsonPropertyName("server_port")] int ServerPort);
}
