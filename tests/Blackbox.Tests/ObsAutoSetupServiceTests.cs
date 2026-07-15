using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class ObsAutoSetupServiceTests
{
    [Fact]
    public async Task SetupAsync_tests_connection_then_applies_plan()
    {
        var obs = new SetupObsController(ObsConnectionStatus.Connected());
        var service = new ObsAutoSetupService(obs, new ObsSetupPlanner(), NullLogger<ObsAutoSetupService>.Instance);

        var result = await service.SetupAsync(new ObsConnectionSettings(), new RecordingSettings { RecordingLocation = "C:\\Recordings" });

        Assert.True(result.IsConnected);
        Assert.Equal(["TestConnection", "ApplySetup:5"], obs.Calls);
    }

    [Fact]
    public async Task SetupAsync_does_not_apply_plan_when_connection_fails()
    {
        var obs = new SetupObsController(ObsConnectionStatus.Failed("OBS is not reachable."));
        var service = new ObsAutoSetupService(obs, new ObsSetupPlanner(), NullLogger<ObsAutoSetupService>.Instance);

        var result = await service.SetupAsync(new ObsConnectionSettings(), new RecordingSettings { RecordingLocation = "C:\\Recordings" });

        Assert.False(result.IsConnected);
        Assert.Equal(["TestConnection"], obs.Calls);
    }

    private sealed class SetupObsController(ObsConnectionStatus status) : IObsController
    {
        public List<string> Calls { get; } = [];

        public Task LaunchAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ObsConnectionStatus> TestConnectionAsync(ObsConnectionSettings settings, CancellationToken cancellationToken = default)
        {
            Calls.Add("TestConnection");
            return Task.FromResult(status);
        }

        public Task ApplySetupPlanAsync(ObsConnectionSettings settings, ObsSetupPlan plan, CancellationToken cancellationToken = default)
        {
            Calls.Add($"ApplySetup:{plan.Sources.Count}");
            return Task.CompletedTask;
        }

        public Task ConfigureSegmentedRecordingAsync(string recordingDirectory, int segmentMinutes, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ConfigureAudioAsync(AudioRoutingProfile profile, MicrophoneProcessingSettings microphoneSettings, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartRecordingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopRecordingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
