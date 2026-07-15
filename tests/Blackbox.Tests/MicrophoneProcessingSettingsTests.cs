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
}
