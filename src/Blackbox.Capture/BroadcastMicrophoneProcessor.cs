using Blackbox.Domain;
using NAudio.Dsp;
using SpeexDSPSharp.Core;

namespace Blackbox.Capture;

public sealed class BroadcastMicrophoneProcessor : IDisposable
{
    public const int SampleRate = 48_000;
    public const int FrameSamples = 960;
    private const double MinimumLevel = 1e-8;
    private static readonly double ExpanderAttack = Coefficient(4);
    private static readonly double ExpanderRelease = Coefficient(140);
    private static readonly double ExpanderGainAttack = Coefficient(8);
    private static readonly double ExpanderGainRelease = Coefficient(120);
    private static readonly double DeEsserAttack = Coefficient(2);
    private static readonly double DeEsserRelease = Coefficient(70);
    private static readonly double DeEsserGainRelease = Coefficient(80);
    private static readonly double CompressorAttack = Coefficient(8);
    private static readonly double CompressorRelease = Coefficient(100);
    private static readonly double CompressorGainRelease = Coefficient(110);
    private static readonly double LimiterRelease = Coefficient(60);
    private static readonly double MinimumExpanderGain = DbToLinear(-36);
    private static readonly double MinimumDeEsserGain = DbToLinear(-8);
    private readonly SpeexDSPPreprocessor noiseSuppressor = new(FrameSamples, SampleRate);
    private MicrophoneProcessingSettings settings;
    private BiQuadFilter? highPass;
    private BiQuadFilter? lowMidEq;
    private BiQuadFilter? presenceEq;
    private BiQuadFilter? deEsserDetector;
    private double expanderEnvelope;
    private double expanderGain = 1;
    private double compressorEnvelope;
    private double compressorGain = 1;
    private double deEsserEnvelope;
    private double deEsserGain = 1;
    private double limiterGain = 1;
    private bool disposed;

    public BroadcastMicrophoneProcessor(MicrophoneProcessingSettings settings)
    {
        this.settings = Validate(settings);
        ConfigureFilters();
        ConfigureNoiseSuppressor();
    }

    public MicrophoneProcessingSettings Settings => settings;

    public void Update(MicrophoneProcessingSettings next)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (settings == next)
        {
            return;
        }
        settings = Validate(next);
        ConfigureFilters();
        ConfigureNoiseSuppressor();
    }

    public void Process(Span<float> frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frame.Length != FrameSamples)
        {
            throw new ArgumentException($"Microphone frames must contain exactly {FrameSamples} samples.", nameof(frame));
        }

        var inputGain = DbToLinear(settings.InputGainDb);
        var expanderThreshold = DbToLinear(settings.ExpanderThresholdDb);
        var expanderExponent = settings.ExpanderRatio - 1;
        var deEsserThreshold = DbToLinear(settings.DeEsserThresholdDb);
        var compressorThreshold = DbToLinear(settings.CompressorThresholdDb);
        var compressorExponent = 1 - 1 / settings.CompressorRatio;
        var makeupGain = DbToLinear(settings.CompressorMakeupGainDb);
        var limiterCeiling = DbToLinear(settings.LimiterThresholdDb);
        for (var i = 0; i < frame.Length; i++)
        {
            var sample = Sanitize(frame[i]) * inputGain;
            frame[i] = settings.ProcessingEnabled && settings.HighPassEnabled
                ? highPass!.Transform((float)sample)
                : (float)sample;
        }

        if (settings.ProcessingEnabled && settings.NoiseSuppressionEnabled)
        {
            Span<short> pcm = stackalloc short[FrameSamples];
            for (var i = 0; i < frame.Length; i++)
            {
                pcm[i] = (short)Math.Round(Math.Clamp(frame[i], -1, 1) * short.MaxValue);
            }

            noiseSuppressor.Run(pcm);
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = pcm[i] / 32768f;
            }
        }

        for (var i = 0; i < frame.Length; i++)
        {
            var sample = frame[i];
            if (settings.ProcessingEnabled && settings.ExpanderEnabled)
            {
                expanderEnvelope = FollowEnvelope(expanderEnvelope, Math.Abs(sample), ExpanderAttack, ExpanderRelease);
                var target = expanderEnvelope < expanderThreshold
                    ? Math.Max(MinimumExpanderGain, Math.Pow(Math.Max(MinimumLevel, expanderEnvelope) / expanderThreshold, expanderExponent))
                    : 1;
                expanderGain = SmoothGain(expanderGain, target, ExpanderGainAttack, ExpanderGainRelease);
                sample *= (float)expanderGain;
            }

            if (settings.ProcessingEnabled)
            {
                sample = lowMidEq!.Transform(sample);
                sample = presenceEq!.Transform(sample);
            }

            if (settings.ProcessingEnabled && settings.DeEsserEnabled)
            {
                var sibilance = Math.Abs(deEsserDetector!.Transform(sample));
                deEsserEnvelope = FollowEnvelope(deEsserEnvelope, sibilance, DeEsserAttack, DeEsserRelease);
                var target = deEsserEnvelope > deEsserThreshold
                    ? Math.Max(MinimumDeEsserGain, Math.Pow(deEsserThreshold / deEsserEnvelope, 0.65))
                    : 1;
                deEsserGain = SmoothGain(deEsserGain, target, DeEsserAttack, DeEsserGainRelease);
                sample *= (float)deEsserGain;
            }

            if (settings.ProcessingEnabled && settings.CompressorEnabled)
            {
                compressorEnvelope = FollowEnvelope(compressorEnvelope, Math.Abs(sample), CompressorAttack, CompressorRelease);
                var target = compressorEnvelope > compressorThreshold
                    ? makeupGain * Math.Pow(compressorThreshold / compressorEnvelope, compressorExponent)
                    : makeupGain;
                compressorGain = SmoothGain(compressorGain, target, CompressorAttack, CompressorGainRelease);
                sample *= (float)compressorGain;
            }

            if (settings.ProcessingEnabled && settings.LimiterEnabled)
            {
                var target = Math.Abs(sample) > limiterCeiling ? limiterCeiling / Math.Abs(sample) : 1;
                limiterGain = target < limiterGain
                    ? target
                    : SmoothGain(limiterGain, target, 0, LimiterRelease);
                sample *= (float)limiterGain;
            }

            frame[i] = Math.Clamp(Sanitize(sample), -1, 1);
        }
    }

    private void ConfigureFilters()
    {
        highPass = BiQuadFilter.HighPassFilter(SampleRate, (float)settings.HighPassCutoffHz, 0.7071f);
        lowMidEq = BiQuadFilter.PeakingEQ(SampleRate, 250, 0.9f, (float)settings.LowMidEqDb);
        presenceEq = BiQuadFilter.PeakingEQ(SampleRate, 3_200, 0.85f, (float)settings.PresenceEqDb);
        deEsserDetector = BiQuadFilter.HighPassFilter(SampleRate, 5_200, 0.7071f);
    }

    private void ConfigureNoiseSuppressor()
    {
        var enabled = settings.NoiseSuppressionEnabled ? 1 : 0;
        var disabled = 0;
        var suppression = (int)Math.Round(settings.NoiseSuppressionDb);
        noiseSuppressor.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_DENOISE, ref enabled);
        noiseSuppressor.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_NOISE_SUPPRESS, ref suppression);
        noiseSuppressor.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_AGC, ref disabled);
    }

    private static MicrophoneProcessingSettings Validate(MicrophoneProcessingSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        return value;
    }

    private static double FollowEnvelope(double current, double input, double attack, double release)
    {
        var coefficient = input > current ? attack : release;
        return coefficient * current + (1 - coefficient) * input;
    }

    private static double SmoothGain(double current, double target, double attack, double release)
    {
        var coefficient = target < current ? attack : release;
        return coefficient * current + (1 - coefficient) * target;
    }

    private static double DbToLinear(double value) => Math.Pow(10, value / 20);

    private static double Coefficient(double milliseconds) => Math.Exp(-1 / (SampleRate * milliseconds / 1000));

    private static float Sanitize(double value) => double.IsFinite(value) ? (float)value : 0;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        noiseSuppressor.Dispose();
    }
}
