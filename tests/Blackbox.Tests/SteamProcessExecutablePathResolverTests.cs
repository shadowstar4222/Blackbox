using Blackbox.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class SteamProcessExecutablePathResolverTests
{
    [Fact]
    public void Resolve_throttles_repeated_forced_misses_and_caches_root_discovery()
    {
        using var fixture = new LogFixture();
        fixture.Append(fixture.Added(42));
        Assert.Equal(fixture.ExecutablePath, fixture.Resolver.Resolve(42, "game.exe"));
        fixture.Append("AppID 1 no longer tracking PID 42\n");

        for (var index = 0; index < 100; index++)
        {
            Assert.Null(fixture.Resolver.Resolve(999, "unresolved.exe", forceRefresh: true));
            Assert.Equal(fixture.ExecutablePath, fixture.Resolver.Resolve(42, "game.exe", forceRefresh: true));
        }

        fixture.Advance();
        Assert.Null(fixture.Resolver.Resolve(42, "game.exe", forceRefresh: true));
        Assert.Equal(1, fixture.RootReads);
    }

    [Fact]
    public void Resolve_does_not_reopen_an_unchanged_log_even_when_forced()
    {
        using var fixture = new LogFixture();
        fixture.Append(fixture.Added(42));
        Assert.Equal(fixture.ExecutablePath, fixture.Resolver.Resolve(42, "game.exe"));

        using var exclusive = new FileStream(fixture.LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fixture.Advance();

        Assert.Equal(fixture.ExecutablePath, fixture.Resolver.Resolve(42, "game.exe", forceRefresh: true));
    }

    [Fact]
    public void Resolve_reads_appends_without_losing_long_running_processes_outside_the_tail()
    {
        using var fixture = new LogFixture();
        fixture.Append(fixture.Added(42));
        Assert.Equal(fixture.ExecutablePath, fixture.Resolver.Resolve(42, "game.exe"));
        var chunk = string.Concat(Enumerable.Repeat("Unrelated Steam log line\n", 6000));
        for (var index = 0; index < 10; index++)
        {
            fixture.Append(chunk);
            fixture.Advance();
            Assert.Equal(fixture.ExecutablePath, fixture.Resolver.Resolve(42, "game.exe"));
        }

        Assert.True(new FileInfo(fixture.LogPath).Length > SteamProcessExecutablePathResolver.MaximumReadBytes);
        fixture.Append(fixture.Added(43) + "AppID 1 no longer tracking PID 42\n");
        fixture.Advance();
        Assert.Null(fixture.Resolver.Resolve(42, "game.exe"));
        Assert.Equal(fixture.ExecutablePath, fixture.Resolver.Resolve(43, "game.exe"));
    }

    [Fact]
    public void Resolve_only_reads_a_bounded_tail_of_an_oversized_log_on_startup()
    {
        using var fixture = new LogFixture();
        fixture.Append(fixture.Added(41));
        fixture.Append(new string('x', SteamProcessExecutablePathResolver.MaximumReadBytes * 2) + "\n");
        fixture.Append(fixture.Added(42));

        Assert.Null(fixture.Resolver.Resolve(41, "game.exe"));
        Assert.Equal(fixture.ExecutablePath, fixture.Resolver.Resolve(42, "game.exe"));
    }

    [Fact]
    public void Resolve_waits_for_complete_lines_and_handles_split_appends()
    {
        using var fixture = new LogFixture();
        var line = fixture.Added(42);
        fixture.Append(line[..^1]);
        Assert.Null(fixture.Resolver.Resolve(42, "game.exe"));
        fixture.Append("\nAppID 1 no longer track");
        fixture.Advance();
        Assert.Equal(fixture.ExecutablePath, fixture.Resolver.Resolve(42, "game.exe"));
        fixture.Append("ing PID 42\n");
        fixture.Advance();

        Assert.Null(fixture.Resolver.Resolve(42, "game.exe"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_discards_stale_mappings_after_truncation_or_replacement(bool regrow)
    {
        using var fixture = new LogFixture();
        fixture.Append(fixture.Added(41));
        Assert.Equal(fixture.ExecutablePath, fixture.Resolver.Resolve(41, "game.exe"));
        File.WriteAllText(fixture.LogPath, regrow ? new string('x', 500) + "\n" + fixture.Added(42) : "\n");
        fixture.Advance();

        Assert.Null(fixture.Resolver.Resolve(41, "game.exe"));
        if (regrow)
        {
            Assert.Equal(fixture.ExecutablePath, fixture.Resolver.Resolve(42, "game.exe"));
        }
    }

    [Fact]
    public void Resolve_drops_missing_logs_and_retries_after_transient_read_failure()
    {
        using var fixture = new LogFixture();
        fixture.Append(fixture.Added(42));
        Assert.Equal(fixture.ExecutablePath, fixture.Resolver.Resolve(42, "game.exe"));
        fixture.Append("Updated log\n");
        using (var exclusive = new FileStream(fixture.LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fixture.Advance();
            Assert.Null(fixture.Resolver.Resolve(42, "game.exe"));
        }

        fixture.Advance();
        Assert.Equal(fixture.ExecutablePath, fixture.Resolver.Resolve(42, "game.exe"));
        File.Delete(fixture.LogPath);
        fixture.Advance();
        Assert.Null(fixture.Resolver.Resolve(42, "game.exe"));
    }

    [Fact]
    public void Resolve_rejects_wrong_pid_wrong_executable_and_missing_executable()
    {
        using var fixture = new LogFixture();
        fixture.Append(fixture.Added(42));

        Assert.Null(fixture.Resolver.Resolve(43, "game.exe"));
        Assert.Null(fixture.Resolver.Resolve(42, "HD2Arsenal.exe"));
        File.Delete(fixture.ExecutablePath);
        Assert.Null(fixture.Resolver.Resolve(42, "game.exe"));
    }

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

    [Fact]
    public void SelectActiveProcessPath_can_recover_a_protected_process_without_a_snapshot_name()
    {
        const string path =
            "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Helldivers 2\\bin\\helldivers2.exe";
        IReadOnlyDictionary<int, string> activePaths = new Dictionary<int, string>
        {
            [16668] = path
        };

        var result = SteamProcessExecutablePathResolver.SelectActiveProcessPath(
            activePaths,
            16668,
            string.Empty,
            _ => true);

        Assert.Equal(path, result);
    }

    [Fact]
    public void SelectActiveProcessPath_does_not_substitute_a_different_process()
    {
        IReadOnlyDictionary<int, string> activePaths = new Dictionary<int, string>
        {
            [16668] =
                "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Helldivers 2\\bin\\helldivers2.exe"
        };

        var result = SteamProcessExecutablePathResolver.SelectActiveProcessPath(
            activePaths,
            16668,
            "HD2Arsenal.exe",
            _ => true);

        Assert.Null(result);
    }
    private sealed class LogFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("Blackbox-Steam-").FullName;
        private readonly DiscoveryTestTimeProvider _time = new();
        public string LogPath => Path.Combine(_root, "gameprocess_log.txt");
        public string ExecutablePath => Path.Combine(_root, "game.exe");
        public SteamProcessExecutablePathResolver Resolver { get; }
        public int RootReads { get; private set; }

        public LogFixture()
        {
            File.WriteAllText(ExecutablePath, "test executable");
            Resolver = new SteamProcessExecutablePathResolver(
                NullLogger<SteamProcessExecutablePathResolver>.Instance, _time, () =>
                {
                    RootReads++;
                    return [LogPath];
                });
        }

        public string Added(int pid) => $"AppID 1 adding PID {pid} as a tracked process \"\"{ExecutablePath}\"\"\n";
        public void Append(string text) => File.AppendAllText(LogPath, text);
        public void Advance() => _time.Advance(TimeSpan.FromSeconds(1));
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
