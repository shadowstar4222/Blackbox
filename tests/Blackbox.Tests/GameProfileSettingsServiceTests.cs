using System.Text.Json;
using Blackbox.Domain;
using Blackbox.Infrastructure;
using Blackbox.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blackbox.Tests;

public sealed class GameProfileSettingsServiceTests
{
    [Fact]
    public async Task Resolve_and_metadata_capture_the_exact_applied_profile()
    {
        var root = Path.Combine(Path.GetTempPath(), "blackbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var profile = new GameProfile(
            "C:\\Games\\Example\\Example.exe",
            "Example",
            true,
            DateTimeOffset.Parse("2026-07-25T12:00:00Z"),
            DateTimeOffset.Parse("2026-07-25T12:00:00Z"))
        {
            Overrides = new GameProfileOverrides
            {
                FramesPerSecond = 120,
                AudioBitrateKbps = 320,
                CaptureDelaySeconds = 3,
                CaptureMicrophone = false
            }
        };
        var service = new GameProfileSettingsService(
            new FixedGameProfileRepository(profile),
            new UserExperienceSettingsStore(new UserExperienceOptions
            {
                SettingsPath = Path.Combine(root, "experience.json")
            }),
            new MicrophoneConfigurationStore(),
            new RecordingSettings { RecordingLocation = root },
            new AutomaticCaptureOptions(),
            new FixedClock(DateTimeOffset.Parse("2026-07-25T12:30:00Z")),
            NullLogger<GameProfileSettingsService>.Instance);
        var target = new GameCaptureTarget(
            42,
            profile.ExecutablePath,
            "Example.exe",
            profile.DisplayName,
            "Example:Window:Example.exe",
            GameDetectionSource.ConfiguredExecutable);

        try
        {
            var effective = await service.ResolveAsync(target);
            service.WriteSessionMetadata(root, target, effective);

            Assert.Equal(120, effective.RecordingQuality.FramesPerSecond);
            Assert.Equal(320, effective.RecordingQuality.AudioBitrateKbps);
            Assert.Equal(TimeSpan.FromSeconds(3), effective.CaptureDelay);
            Assert.False(effective.CaptureMicrophone);
            using var metadata = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(root, "blackbox-session.json")));
            Assert.Equal(
                "Example",
                metadata.RootElement.GetProperty("profileName").GetString());
            Assert.Equal(
                120,
                metadata.RootElement
                    .GetProperty("effectiveSettings")
                    .GetProperty("recordingQuality")
                    .GetProperty("framesPerSecond")
                    .GetInt32());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FixedGameProfileRepository(GameProfile profile) : IGameProfileRepository
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<GameProfile>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameProfile>>([profile]);

        public Task UpsertAsync(
            GameProfile updated,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            string executablePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
