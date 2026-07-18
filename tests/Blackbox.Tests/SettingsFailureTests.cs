using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class SettingsFailureTests
{
    [Fact]
    public void Failed_atomic_writes_do_not_publish_unpersisted_settings()
    {
        var root = CreateRoot();
        try
        {
            var blockedParent = Path.Combine(root, "not-a-directory");
            File.WriteAllText(blockedParent, "file");
            var options = new ObsProvisioningOptions
            {
                PortableRootDirectory = Path.Combine(root, "obs"),
                ConnectionSettingsPath = Path.Combine(blockedParent, "connection.json"),
                MicrophoneSettingsPath = Path.Combine(blockedParent, "microphone.json"),
                AutomaticCaptureSettingsPath = Path.Combine(blockedParent, "automatic.json")
            };
            var connection = new ObsConnectionSettingsProvider(options);
            var microphone = new MicrophoneConfigurationStore(options);
            var automatic = new AutomaticCapturePreferenceStore(options);

            Assert.ThrowsAny<IOException>(() => connection.Set(
                new ObsConnectionSettings { Port = 5566, Password = "new-password" }));
            Assert.ThrowsAny<IOException>(() => microphone.Save(new MicrophoneConfiguration
            {
                DeviceId = "new-device",
                DeviceName = "New microphone"
            }));
            Assert.ThrowsAny<IOException>(() => automatic.Save(true));

            Assert.Equal(4455, connection.Current.Port);
            Assert.NotEqual("new-password", connection.Current.Password);
            Assert.NotEqual("new-device", microphone.Current.DeviceId);
            Assert.False(automatic.WasEnabled);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
