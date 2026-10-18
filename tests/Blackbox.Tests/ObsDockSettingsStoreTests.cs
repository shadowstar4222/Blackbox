using Blackbox.Dock;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class ObsDockSettingsStoreTests
{
    [Fact]
    public void Settings_are_persistent_and_use_a_private_capability_token()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "obs-dock.json");
            var firstStore = CreateStore(path);
            var first = firstStore.LoadOrCreate();
            firstStore.UpdatePort(54321);

            var second = CreateStore(path).LoadOrCreate();

            Assert.Equal(64, first.Token.Length);
            Assert.Equal(first.Token, second.Token);
            Assert.Equal(54321, second.Port);
            Assert.All(first.Token, character => Assert.True(Uri.IsHexDigit(character)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ObsDockSettingsStore CreateStore(string path) =>
        new(
            new ObsDockOptions { SettingsPath = path },
            NullLogger<ObsDockSettingsStore>.Instance);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
