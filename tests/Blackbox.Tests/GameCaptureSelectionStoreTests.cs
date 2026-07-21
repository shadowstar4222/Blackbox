using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class GameCaptureSelectionStoreTests
{
    [Fact]
    public void Save_and_clear_persist_the_preferred_capture_target()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "capture-selection.json");
        var options = new ObsProvisioningOptions
        {
            PortableRootDirectory = Path.Combine(root, "obs"),
            GameCaptureSelectionSettingsPath = path
        };
        var selection = new GameCaptureSelection(
            "C:\\Games\\Example\\Launcher.exe",
            "C:\\Games\\Example\\Game.exe",
            "Example Game");
        var replacement = new GameCaptureSelection(
            "C:\\Games\\Second\\Second.exe",
            "C:\\Games\\Second\\Second.exe",
            "Second Game");
        try
        {
            var store = new GameCaptureSelectionStore(options);

            store.Save(selection);

            Assert.Equal(selection, new GameCaptureSelectionStore(options).Current);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));

            store.Save(replacement);

            Assert.Equal(replacement, new GameCaptureSelectionStore(options).Current);

            store.Clear();

            Assert.Null(store.Current);
            Assert.Null(new GameCaptureSelectionStore(options).Current);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
