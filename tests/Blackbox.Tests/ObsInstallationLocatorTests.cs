using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class ObsInstallationLocatorTests
{
    [Fact]
    public async Task FindExistingInstallation_finds_an_additional_OBS_root()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var executable = Path.Combine(testRoot, "bin", "64bit", "obs64.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            Directory.CreateDirectory(Path.Combine(testRoot, "data"));
            Directory.CreateDirectory(Path.Combine(testRoot, "obs-plugins"));
            await File.WriteAllTextAsync(executable, "test executable");
            var locator = new ObsInstallationLocator(new ObsProvisioningOptions
            {
                PortableRootDirectory = Path.Combine(testRoot, "unused-portable-root"),
                SearchSystemInstallations = false,
                AdditionalInstallationSearchPaths = [testRoot]
            });

            var installation = locator.FindExistingInstallation();

            Assert.NotNull(installation);
            Assert.Equal(testRoot, installation.RootDirectory);
            Assert.Equal(executable, installation.ExecutablePath);
        }
        finally
        {
            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task FindExistingInstallation_ignores_an_incomplete_OBS_root()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var executable = Path.Combine(testRoot, "bin", "64bit", "obs64.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllTextAsync(executable, "test executable");
            var locator = new ObsInstallationLocator(new ObsProvisioningOptions
            {
                PortableRootDirectory = Path.Combine(testRoot, "unused-portable-root"),
                SearchSystemInstallations = false,
                AdditionalInstallationSearchPaths = [testRoot]
            });

            var installation = locator.FindExistingInstallation();

            Assert.Null(installation);
        }
        finally
        {
            Directory.Delete(testRoot, true);
        }
    }
}
