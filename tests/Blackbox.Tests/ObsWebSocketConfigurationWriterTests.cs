using System.Text.Json;
using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class ObsWebSocketConfigurationWriterTests
{
    [Fact]
    public async Task Write_enables_the_private_server_with_current_connection_settings()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configurationPath = Path.Combine(
                testRoot,
                "config",
                "obs-studio",
                "plugin_config",
                "obs-websocket",
                "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
            await File.WriteAllTextAsync(configurationPath, "{\"server_enabled\":false,\"server_port\":4455}");
            var settings = new ObsConnectionSettings
            {
                Port = 55698,
                Password = "private-test-password"
            };

            ObsWebSocketConfigurationWriter.Write(testRoot, settings);

            await using var stream = File.OpenRead(configurationPath);
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            Assert.True(root.GetProperty("server_enabled").GetBoolean());
            Assert.True(root.GetProperty("auth_required").GetBoolean());
            Assert.False(root.GetProperty("first_load").GetBoolean());
            Assert.Equal(55698, root.GetProperty("server_port").GetInt32());
            Assert.Equal("private-test-password", root.GetProperty("server_password").GetString());
        }
        finally
        {
            Directory.Delete(testRoot, true);
        }
    }
}
