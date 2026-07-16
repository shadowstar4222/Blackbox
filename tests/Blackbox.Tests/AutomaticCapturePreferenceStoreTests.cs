using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class AutomaticCapturePreferenceStoreTests
{
    [Fact]
    public void Save_persists_interrupted_automatic_capture_state_atomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "automatic-capture.json");
        var options = new ObsProvisioningOptions
        {
            PortableRootDirectory = Path.Combine(root, "obs"),
            AutomaticCaptureSettingsPath = path
        };
        try
        {
            var store = new AutomaticCapturePreferenceStore(options);

            store.Save(true);
            Assert.True(new AutomaticCapturePreferenceStore(options).WasEnabled);

            store.Save(false);
            Assert.False(new AutomaticCapturePreferenceStore(options).WasEnabled);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));
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
