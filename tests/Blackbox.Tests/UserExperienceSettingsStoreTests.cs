using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class UserExperienceSettingsStoreTests
{
    [Fact]
    public void Missing_settings_use_safe_desktop_defaults()
    {
        var root = CreateRoot();
        try
        {
            var store = CreateStore(root);

            Assert.False(store.Current.StartWithWindows);
            Assert.True(store.Current.CloseToTray);
            Assert.False(store.Current.WatchRememberedGames);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Save_persists_every_experience_preference_atomically()
    {
        var root = CreateRoot();
        try
        {
            var store = CreateStore(root);
            var expected = new UserExperienceSettings
            {
                StartWithWindows = true,
                CloseToTray = false,
                WatchRememberedGames = true
            };

            store.Save(expected);

            Assert.Equal(expected, CreateStore(root).Current);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Corrupted_settings_fall_back_to_safe_defaults()
    {
        var root = CreateRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "experience.json"), "{ invalid");

            var settings = CreateStore(root).Current;

            Assert.False(settings.StartWithWindows);
            Assert.True(settings.CloseToTray);
            Assert.False(settings.WatchRememberedGames);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Failed_save_does_not_publish_unpersisted_preferences()
    {
        var root = CreateRoot();
        try
        {
            var blockedParent = Path.Combine(root, "not-a-directory");
            File.WriteAllText(blockedParent, "file");
            var store = new UserExperienceSettingsStore(new UserExperienceOptions
            {
                SettingsPath = Path.Combine(blockedParent, "experience.json")
            });

            Assert.ThrowsAny<IOException>(() => store.Save(new UserExperienceSettings
            {
                StartWithWindows = true,
                CloseToTray = false,
                WatchRememberedGames = true
            }));
            Assert.Equal(new UserExperienceSettings(), store.Current);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static UserExperienceSettingsStore CreateStore(string root) =>
        new(new UserExperienceOptions
        {
            SettingsPath = Path.Combine(root, "experience.json")
        });

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "blackbox-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
