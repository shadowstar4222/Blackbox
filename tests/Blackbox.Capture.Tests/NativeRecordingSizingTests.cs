using Blackbox.Domain;

namespace Blackbox.Capture.Tests;

public sealed class NativeRecordingSizingTests
{
    [Theory]
    [InlineData(RecordingResolution.FullHd1080, 3840, 2160, 1920, 1080)]
    [InlineData(RecordingResolution.Hd720, 2560, 1080, 1280, 540)]
    [InlineData(RecordingResolution.FullHd1080, 1080, 1920, 606, 1080)]
    [InlineData(RecordingResolution.UltraHd2160, 801, 601, 800, 600)]
    [InlineData(RecordingResolution.MatchApplication, 7680, 2160, 3840, 1080)]
    [InlineData(RecordingResolution.MatchApplication, 1280, 720, 1280, 720)]
    public void Resolution_preserves_aspect_ratio_without_upscaling(RecordingResolution resolution, int inputWidth, int inputHeight, int width, int height) =>
        Assert.Equal((width, height), NativeRecordingSizing.Resolve(resolution, inputWidth, inputHeight));

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(63, 500)]
    public void Tiny_windows_are_rejected_before_launch(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeRecordingSizing.Resolve(RecordingResolution.FullHd1080, width, height));

    [Fact]
    public void Invalid_resolution_does_not_silently_change_quality() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeRecordingSizing.Resolve((RecordingResolution)987, 1920, 1080));

    [Fact]
    public void Bitrate_is_bounded_for_small_and_4k_windows()
    {
        Assert.Equal(2_000_000, NativeRecordingSizing.VideoBitrate(640, 480, 30));
        Assert.Equal(50_000_000, NativeRecordingSizing.VideoBitrate(3840, 2160, 120));
    }
}
