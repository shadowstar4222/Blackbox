using Blackbox.Domain;

namespace Blackbox.Capture.Tests;

public sealed class BroadcastMicrophoneProcessorTests
{
    [Theory]
    [InlineData(MicrophoneVoicePreset.Natural)]
    [InlineData(MicrophoneVoicePreset.Broadcast)]
    [InlineData(MicrophoneVoicePreset.StrongNoiseRemoval)]
    [InlineData(MicrophoneVoicePreset.Custom)]
    public void Every_preset_is_a_valid_processing_chain(MicrophoneVoicePreset preset)
    {
        var settings = MicrophoneProcessingSettings.ForPreset(preset, 2.5);

        settings.Validate();

        Assert.Equal(preset, settings.Preset);
        Assert.Equal(2.5, settings.InputGainDb);
    }

    [Fact]
    public void Bypass_keeps_only_input_gain_active()
    {
        using var processor = new BroadcastMicrophoneProcessor(new MicrophoneProcessingSettings
        {
            ProcessingEnabled = false,
            InputGainDb = 6
        });
        var frame = Enumerable.Repeat(0.1f, BroadcastMicrophoneProcessor.FrameSamples).ToArray();

        processor.Process(frame);

        Assert.All(frame, sample => Assert.InRange(sample, 0.1994f, 0.1997f));
    }

    [Fact]
    public void Limiter_keeps_boosted_audio_below_its_ceiling()
    {
        var settings = FlatSettings() with
        {
            InputGainDb = 18,
            LimiterEnabled = true,
            LimiterThresholdDb = -3
        };
        using var processor = new BroadcastMicrophoneProcessor(settings);
        var frame = Enumerable.Range(0, BroadcastMicrophoneProcessor.FrameSamples)
            .Select(index => 0.8f * MathF.Sin(2 * MathF.PI * 1000 * index / BroadcastMicrophoneProcessor.SampleRate))
            .ToArray();

        processor.Process(frame);

        var ceiling = Math.Pow(10, settings.LimiterThresholdDb / 20);
        Assert.True(frame.Max(sample => Math.Abs(sample)) <= ceiling + 0.0001);
    }

    [Fact]
    public void High_pass_filter_reduces_low_frequency_fan_rumble()
    {
        using var unfiltered = new BroadcastMicrophoneProcessor(FlatSettings());
        using var filtered = new BroadcastMicrophoneProcessor(FlatSettings() with
        {
            HighPassEnabled = true,
            HighPassCutoffHz = 100
        });
        double unfilteredEnergy = 0;
        double filteredEnergy = 0;
        var sampleIndex = 0;

        for (var frameIndex = 0; frameIndex < 80; frameIndex++)
        {
            var input = new float[BroadcastMicrophoneProcessor.FrameSamples];
            for (var i = 0; i < input.Length; i++, sampleIndex++)
            {
                input[i] = 0.25f * MathF.Sin(2 * MathF.PI * 40 * sampleIndex / BroadcastMicrophoneProcessor.SampleRate);
            }

            var clean = (float[])input.Clone();
            unfiltered.Process(clean);
            filtered.Process(input);
            if (frameIndex >= 60)
            {
                unfilteredEnergy += clean.Sum(sample => sample * sample);
                filteredEnergy += input.Sum(sample => sample * sample);
            }
        }

        Assert.True(filteredEnergy < unfilteredEnergy * 0.1);
    }

    [Fact]
    public void Reapplying_identical_settings_preserves_filter_history()
    {
        var settings = FlatSettings() with { HighPassEnabled = true };
        using var unchanged = new BroadcastMicrophoneProcessor(settings);
        using var reapplied = new BroadcastMicrophoneProcessor(settings);
        var expected = new float[BroadcastMicrophoneProcessor.FrameSamples];
        var actual = new float[expected.Length];
        for (var frameIndex = 0; frameIndex < 20; frameIndex++)
        {
            Array.Fill(expected, 0.1f);
            Array.Fill(actual, 0.1f);
            unchanged.Process(expected);
            reapplied.Update(settings with { });
            reapplied.Process(actual);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Optimized_dynamics_preserves_the_original_broadcast_reference()
    {
        using var processor = new BroadcastMicrophoneProcessor(new MicrophoneProcessingSettings());
        var input = new float[BroadcastMicrophoneProcessor.FrameSamples];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = 0.2f * MathF.Sin(2 * MathF.PI * 200 * i / BroadcastMicrophoneProcessor.SampleRate) +
                       0.003f * MathF.Sin(2 * MathF.PI * 75 * i / BroadcastMicrophoneProcessor.SampleRate);
        }

        var frame = new float[input.Length];
        for (var i = 0; i < 10500; i++)
        {
            input.CopyTo(frame, 0);
            processor.Process(frame);
        }

        Assert.InRange(frame.Max(static sample => Math.Abs(sample)), 0.013008f, 0.013011f);
    }

    [Fact]
    public void Processor_rejects_non_twenty_millisecond_frames()
    {
        using var processor = new BroadcastMicrophoneProcessor(new MicrophoneProcessingSettings());

        Assert.Throws<ArgumentException>(() => processor.Process(new float[100]));
    }

    private static MicrophoneProcessingSettings FlatSettings() => new()
    {
        Preset = MicrophoneVoicePreset.Custom,
        HighPassEnabled = false,
        NoiseSuppressionEnabled = false,
        ExpanderEnabled = false,
        LowMidEqDb = 0,
        PresenceEqDb = 0,
        CompressorEnabled = false,
        DeEsserEnabled = false,
        LimiterEnabled = false
    };
}
