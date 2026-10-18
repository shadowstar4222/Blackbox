using Blackbox.Dock;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class ObsDockRegistrationServiceTests
{
    private static readonly Uri DockUrl = new(
        "http://127.0.0.1:54321/dock/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/");

    [Fact]
    public void Upsert_preserves_existing_docks_and_is_idempotent()
    {
        const string configuration = """
            [General]
            FirstRun=false

            [BasicWindow]
            PreviewEnabled=true
            ExtraBrowserDocks=[{"title":"Existing","url":"https://example.test/","uuid":"existingdock"}]
            """;

        var updated = ObsDockRegistrationService.UpsertDock(configuration, DockUrl);
        var secondUpdate = ObsDockRegistrationService.UpsertDock(updated, DockUrl);

        Assert.Contains("Existing", updated);
        Assert.Contains("Blackbox Controls", updated);
        Assert.Contains(DockUrl.AbsoluteUri, updated);
        Assert.True(ObsDockRegistrationService.ContainsRegisteredDock(updated, DockUrl));
        Assert.Equal(updated, secondUpdate);
    }

    [Fact]
    public void Upsert_rejects_malformed_existing_dock_json()
    {
        const string configuration = """
            [BasicWindow]
            ExtraBrowserDocks=[not-json
            """;

        var exception = Assert.Throws<InvalidDataException>(() =>
            ObsDockRegistrationService.UpsertDock(configuration, DockUrl));

        Assert.Contains("malformed JSON", exception.Message);
    }

    [Fact]
    public void Upsert_replaces_a_formatted_blackbox_uuid_instead_of_duplicating_it()
    {
        const string configuration = """
            [BasicWindow]
            ExtraBrowserDocks=[{"title":"Old Blackbox","url":"http://127.0.0.1:5000/old/","uuid":"{5d27a418-e12f-4db5-a405-9ccdb66ce11d}"}]
            """;

        var updated = ObsDockRegistrationService.UpsertDock(configuration, DockUrl);

        Assert.DoesNotContain("Old Blackbox", updated);
        Assert.DoesNotContain("http://127.0.0.1:5000/old/", updated);
        Assert.Equal(1, CountOccurrences(updated, ObsDockRegistrationService.DockUuid));
    }

    [Fact]
    public void Register_writes_atomically_and_keeps_a_backup()
    {
        var root = CreateRoot();
        try
        {
            var configurationPath = Path.Combine(root, "config", "obs-studio", "user.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
            const string original = "[BasicWindow]\r\nPreviewEnabled=true\r\n";
            File.WriteAllText(configurationPath, original);
            var settingsStore = CreateStore(root);
            settingsStore.UpdatePort(54321);
            var service = new ObsDockRegistrationService(
                settingsStore,
                NullLogger<ObsDockRegistrationService>.Instance);

            var result = service.Register(root);

            Assert.True(result.WasChanged);
            Assert.Equal(original, File.ReadAllText(configurationPath + ".blackbox-backup"));
            Assert.True(ObsDockRegistrationService.ContainsRegisteredDock(
                File.ReadAllText(configurationPath),
                result.DockUrl));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Register_leaves_malformed_configuration_untouched()
    {
        var root = CreateRoot();
        try
        {
            var configurationPath = Path.Combine(root, "config", "obs-studio", "user.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
            const string malformed = "[BasicWindow]\r\nExtraBrowserDocks={broken\r\n";
            File.WriteAllText(configurationPath, malformed);
            var settingsStore = CreateStore(root);
            settingsStore.UpdatePort(54321);
            var service = new ObsDockRegistrationService(
                settingsStore,
                NullLogger<ObsDockRegistrationService>.Instance);

            Assert.Throws<InvalidDataException>(() => service.Register(root));

            Assert.Equal(malformed, File.ReadAllText(configurationPath));
            Assert.False(File.Exists(configurationPath + ".blackbox-backup"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ObsDockSettingsStore CreateStore(string root) =>
        new(
            new ObsDockOptions { SettingsPath = Path.Combine(root, "obs-dock.json") },
            NullLogger<ObsDockSettingsStore>.Instance);

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(expected, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += expected.Length;
        }

        return count;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
