using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class MicrophoneConfigurationStoreTests
{
    [Fact]
    public void Save_persists_device_and_processing_settings_for_the_next_session()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        var options = new ObsProvisioningOptions
        {
            PortableRootDirectory = Path.Combine(root, "obs-portable"),
            MicrophoneSettingsPath = Path.Combine(root, "microphone.json")
        };

        try
        {
            var firstStore = new MicrophoneConfigurationStore(options);
            firstStore.Save(new MicrophoneConfiguration
            {
                DeviceId = "device-123",
                DeviceName = "Test microphone",
                ProcessingSettings = new MicrophoneProcessingSettings { InputGainDb = 4.5 }
            });

            var secondStore = new MicrophoneConfigurationStore(options);

            Assert.Equal("device-123", secondStore.Current.DeviceId);
            Assert.Equal("Test microphone", secondStore.Current.DeviceName);
            Assert.Equal(4.5, secondStore.Current.ProcessingSettings.InputGainDb);
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
