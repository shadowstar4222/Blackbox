using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class WindowsStartupManagerTests
{
    [Fact]
    public void Startup_command_quotes_the_executable_and_uses_background_mode()
    {
        var executable = Path.Combine(
            Path.GetTempPath(),
            "Blackbox App",
            "Blackbox.App.exe");

        var command = WindowsStartupManager.BuildStartupCommand(executable);

        Assert.Equal($"\"{Path.GetFullPath(executable)}\" --background", command);
    }

    [Fact]
    public void Enable_and_disable_update_the_current_user_registration()
    {
        var registry = new FakeStartupRegistry();
        var manager = new WindowsStartupManager(
            @"C:\Program Files\Blackbox\Blackbox.App.exe",
            registry);

        manager.SetEnabled(true);

        Assert.True(manager.IsEnabled);
        Assert.Equal(
            "\"C:\\Program Files\\Blackbox\\Blackbox.App.exe\" --background",
            registry.Command);

        manager.SetEnabled(false);

        Assert.False(manager.IsEnabled);
        Assert.Null(registry.Command);
    }

    [Fact]
    public void Stale_registration_is_not_reported_as_enabled()
    {
        var registry = new FakeStartupRegistry
        {
            Command = "\"C:\\Old\\Blackbox.App.exe\" --background"
        };
        var manager = new WindowsStartupManager(
            @"C:\Blackbox\Blackbox.App.exe",
            registry);

        Assert.False(manager.IsEnabled);
    }

    private sealed class FakeStartupRegistry :
        WindowsStartupManager.IStartupRegistry
    {
        public string? Command { get; set; }

        public string? Read() => Command;

        public void Write(string command) => Command = command;

        public void Delete() => Command = null;
    }
}
