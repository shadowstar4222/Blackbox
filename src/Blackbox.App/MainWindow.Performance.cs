using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Blackbox.App;

internal sealed record PlaybackBenchmarkConfiguration(string Source, string Output, int SampleSeconds = 15,
    int OpenCloseCycles = 8, string[]? Sources = null);

public partial class MainWindow
{
    internal async Task BenchmarkPlaybackAsync(string configurationPath)
    {
        var config = JsonSerializer.Deserialize<PlaybackBenchmarkConfiguration>(File.ReadAllText(configurationPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing playback configuration.");
        if (!File.Exists(config.Source) || config.SampleSeconds is < 5 or > 120 || config.OpenCloseCycles is < 1 or > 30)
            throw new InvalidDataException("Invalid playback benchmark configuration.");
        Show();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (_isRecoveryBusy) await Task.Delay(50, timeout.Token);
        await SelectNavigationAsync(RecordingsNavButton, OpenRecordingLibraryPageAsync);
        await _recordingLibraryPage!.BenchmarkPlaybackAsync(config);
    }
}

public partial class RecordingLibraryPage
{
    internal async Task BenchmarkPlaybackAsync(PlaybackBenchmarkConfiguration config)
    {
        var startup = Stopwatch.StartNew();
        var session = config.Sources is { Length: > 1 } ? await BuildPlaybackFixtureSessionAsync(config.Sources) : null;
        async Task OpenAsync()
        {
            if (session is null) await OpenMediaFileAsync(config.Source, "Playback benchmark");
            else await OpenPlaybackAsync(session, TimeSpan.Zero, allowMarkers: false);
        }
        await OpenAsync();
        if (_playbackPage is null) throw new InvalidOperationException("Benchmark could not open the in-app player.");
        startup.Stop();
        var output = Path.GetFullPath(config.Output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var result = await _playbackPage.MeasurePlaybackAsync(config.SampleSeconds, Path.GetDirectoryName(output)!);
        await ClosePlaybackAsync();
        var windows = Application.Current.Windows.Count;
        var cycles = new List<object>();
        var releasedPages = new List<WeakReference>();
        using var process = Process.GetCurrentProcess();
        for (var index = 0; index < config.OpenCloseCycles; index++)
        {
            var timer = Stopwatch.StartNew();
            await OpenAsync();
            if (_playbackPage is null) throw new InvalidOperationException("Lifecycle open failed.");
            releasedPages.Add(new WeakReference(_playbackPage));
            await _playbackPage.ValidateFilePreviewAsync();
            await ClosePlaybackAsync();
            await Task.Delay(300);
            process.Refresh();
            if (Application.Current.Windows.Count != windows) throw new InvalidOperationException("Playback leaked an overlay window.");
            cycles.Add(new { Cycle = index + 1, Milliseconds = timer.Elapsed.TotalMilliseconds,
                WorkingSetMiB = process.WorkingSet64 / 1048576d, PrivateMiB = process.PrivateMemorySize64 / 1048576d,
                Handles = process.HandleCount, Windows = windows });
        }
        await Task.Delay(10000);
        var cpu = process.TotalProcessorTime;
        var idle = Stopwatch.StartNew();
        await Task.Delay(5000);
        var releasedCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds / idle.Elapsed.TotalMilliseconds * 100;
        // Collection is a test-only ownership check, never a runtime memory-management policy.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        GC.Collect();
        var retainedPages = releasedPages.Count(static page => page.IsAlive);
        if (retainedPages != 0 && Environment.GetEnvironmentVariable("BLACKBOX_BENCHMARK_HOLD_ON_FAILURE") == "1")
        {
            File.WriteAllText(output + ".retained.txt", $"PID={process.Id}; retained={retainedPages}");
            await Task.Delay(TimeSpan.FromSeconds(120));
        }
        if (retainedPages != 0) throw new InvalidOperationException($"Review retained {retainedPages} closed page object(s).");
        File.WriteAllText(output, JsonSerializer.Serialize(new { Source = config.Source, ProcessId = process.Id,
            OpenRequestMilliseconds = startup.Elapsed.TotalMilliseconds, Playback = result, Lifecycle = cycles,
            ReleasedCpuPercentOfOneCore = releasedCpu, RetainedClosedPagesAfterTestCollection = retainedPages },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
