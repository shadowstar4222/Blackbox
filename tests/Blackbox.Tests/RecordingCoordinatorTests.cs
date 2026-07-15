using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class RecordingCoordinatorTests
{
    [Fact]
    public async Task StartAsync_initializes_database_and_starts_obs_in_order()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        var repository = new InMemorySegmentRepository();
        var obs = new RecordingObsController();
        var coordinator = new RecordingCoordinator(obs, repository, NullLogger<RecordingCoordinator>.Instance);

        await coordinator.StartAsync(new RecordingSettings { RecordingLocation = root, SegmentDurationMinutes = 2 });

        Assert.True(repository.Initialized);
        Assert.Equal(["Launch", "Configure:2", "ConfigureAudio:5", "Start"], obs.Calls);
        Assert.True(Directory.Exists(root));
    }

    private sealed class RecordingObsController : IObsController
    {
        public List<string> Calls { get; } = [];

        public Task LaunchAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("Launch");
            return Task.CompletedTask;
        }

        public Task<ObsConnectionStatus> TestConnectionAsync(ObsConnectionSettings settings, CancellationToken cancellationToken = default)
        {
            Calls.Add("TestConnection");
            return Task.FromResult(ObsConnectionStatus.Connected());
        }

        public Task ApplySetupPlanAsync(ObsConnectionSettings settings, ObsSetupPlan plan, CancellationToken cancellationToken = default)
        {
            Calls.Add($"ApplySetup:{plan.Sources.Count}");
            return Task.CompletedTask;
        }

        public Task ConfigureSegmentedRecordingAsync(string recordingDirectory, int segmentMinutes, CancellationToken cancellationToken = default)
        {
            Calls.Add($"Configure:{segmentMinutes}");
            return Task.CompletedTask;
        }

        public Task ConfigureAudioAsync(AudioRoutingProfile profile, MicrophoneProcessingSettings microphoneSettings, CancellationToken cancellationToken = default)
        {
            Calls.Add($"ConfigureAudio:{profile.Tracks.Count}");
            return Task.CompletedTask;
        }

        public Task StartRecordingAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("Start");
            return Task.CompletedTask;
        }

        public Task<string?> StopRecordingAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("Stop");
            return Task.FromResult<string?>(null);
        }
    }
}
