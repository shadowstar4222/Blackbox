using Blackbox.Infrastructure;

namespace Blackbox.Tests;

public sealed class SteamProcessExecutablePathResolverTests
{
    [Fact]
    public void ParseActiveProcessPaths_recovers_protected_helldivers_process_path()
    {
        var paths = SteamProcessExecutablePathResolver.ParseActiveProcessPaths(
        [
            "[2026-07-19 18:17:02] AppID 553850 adding PID 16668 as a tracked process \"\"C:\\Program Files (x86)\\Steam\\steamapps\\common\\Helldivers 2\\bin\\helldivers2.exe\" --bundle-dir data --release\""
        ]);

        Assert.Equal(
            "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Helldivers 2\\bin\\helldivers2.exe",
            paths[16668]);
    }

    [Fact]
    public void ParseActiveProcessPaths_removes_processes_after_steam_reports_exit()
    {
        var paths = SteamProcessExecutablePathResolver.ParseActiveProcessPaths(
        [
            "AppID 553850 adding PID 16668 as a tracked process \"\"C:\\Games\\helldivers2.exe\" --release\"",
            "AppID 553850 no longer tracking PID 16668, exit code 0"
        ]);

        Assert.Empty(paths);
    }
}
