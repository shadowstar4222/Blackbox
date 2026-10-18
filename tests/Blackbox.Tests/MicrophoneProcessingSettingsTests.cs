using Blackbox.Domain;

namespace Blackbox.Tests;

public sealed class MicrophoneProcessingSettingsTests
{
    [Fact]
    public void Validate_accepts_default_processing_chain()
    {
        new MicrophoneProcessingSettings().Validate();
    }

    [Fact]
    public void Validate_rejects_extreme_compressor_ratio()
    {
        var settings = new MicrophoneProcessingSettings { CompressorRatio = 50 };

        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    [Fact]
    public void Validate_rejects_extreme_input_gain()
    {
        var settings = new MicrophoneProcessingSettings { InputGainDb = 31 };

        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    [Fact]
    public void Validate_rejects_non_finite_filter_values()
    {
        var settings = new MicrophoneProcessingSettings { NoiseSuppressionDb = double.NaN };

        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    [Fact]
    public void Strong_noise_removal_is_more_aggressive_than_broadcast()
    {
        var broadcast = MicrophoneProcessingSettings.ForPreset(MicrophoneVoicePreset.Broadcast);
        var strong = MicrophoneProcessingSettings.ForPreset(MicrophoneVoicePreset.StrongNoiseRemoval);

        Assert.True(strong.NoiseSuppressionDb < broadcast.NoiseSuppressionDb);
        Assert.True(strong.HighPassCutoffHz > broadcast.HighPassCutoffHz);
        Assert.True(strong.ExpanderRatio > broadcast.ExpanderRatio);
    }
}
