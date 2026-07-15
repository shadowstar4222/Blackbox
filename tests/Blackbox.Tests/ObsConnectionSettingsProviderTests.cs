using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class ObsConnectionSettingsProviderTests
{
    [Fact]
    public void Set_persists_connection_for_the_next_app_session()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        var options = new ObsProvisioningOptions
        {
            PortableRootDirectory = Path.Combine(root, "obs-portable"),
            ConnectionSettingsPath = Path.Combine(root, "obs-connection.json")
        };

        try
        {
            var firstProvider = new ObsConnectionSettingsProvider(options);
            firstProvider.Set(new ObsConnectionSettings { Port = 4567, Password = "private-test-password" });

            var secondProvider = new ObsConnectionSettingsProvider(options);

            Assert.Equal(4567, secondProvider.Current.Port);
            Assert.Equal("private-test-password", secondProvider.Current.Password);
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
