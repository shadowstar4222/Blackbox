namespace Blackbox.Export;

/// <summary>An original, loopable instrumental used only by the recording setup window.</summary>
public static class RecordingSetupMusic
{
    public const int SampleRate = 48000;
    public const double DurationSeconds = 80;

    public static void WriteWave(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        const double beat = 0.625; // 96 BPM, 32 bars.
        var count = (int)(SampleRate * DurationSeconds);
        var left = new float[count];
        var right = new float[count];
        int[][] chords = [[50, 57, 60, 64, 69], [43, 53, 59, 64, 69], [48, 55, 59, 62, 67],
            [45, 55, 59, 60, 64], [41, 52, 57, 60, 67], [40, 55, 59, 62, 66],
            [50, 57, 60, 64, 69], [43, 53, 57, 60, 64]];
        int[][] melody = [[76, 81, 79, 76, 74, 72], [74, 79, 81, 83, 81, 79],
            [79, 83, 86, 83, 81, 79], [76, 79, 83, 81, 76, 72],
            [76, 79, 84, 83, 79, 76], [74, 78, 83, 81, 78, 74],
            [77, 81, 84, 81, 79, 76], [74, 77, 81, 79, 74, 71]];
        for (var bar = 0; bar < 32; bar++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chord = chords[bar % 8];
            var start = bar * beat * 4;
            for (var voice = 1; voice < chord.Length; voice++)
            {
                Add(chord[voice], start + voice * .018, beat * 5, .045, (voice - 2.5) * .35, 0);
                Add(chord[voice] + 12, start + voice * beat / 2, beat * 1.6, .047, (voice - 2.5) * -.3, 1);
            }
            Add(chord[0] - 12, start, beat * 1.7, .13, 0, 2);
            Add(chord[0], start + beat * 2.5, beat, .09, -.08, 2);
            for (var note = 0; note < 6; note++)
            {
                var offset = new[] { .5, 1.25, 1.75, 2.5, 3, 3.5 }[note];
                if ((bar + note) % 5 == 0) continue;
                Add(melody[bar % 8][note] + (bar >= 16 && note % 3 == 0 ? 12 : 0),
                    start + offset * beat, beat * 1.8, .065, Math.Sin(bar + note) * .5, 1);
            }
            for (var step = 0; step < 8; step++)
            {
                var at = start + step * beat / 2;
                Drum(at, step % 4 == 0 ? 0 : step % 4 == 2 ? 1 : 2, bar * 8 + step);
            }
        }
        // Circular stereo reflections preserve the reverb tail across the loop boundary.
        var dryLeft = (float[])left.Clone();
        var dryRight = (float[])right.Clone();
        for (var i = 0; i < count; i++)
        {
            if (i % SampleRate == 0) cancellationToken.ThrowIfCancellationRequested();
            left[i] += .22f * dryRight[(i + count - 18000) % count] + .1f * dryLeft[(i + count - 36000) % count];
            right[i] += .22f * dryLeft[(i + count - 24000) % count] + .1f * dryRight[(i + count - 42000) % count];
        }
        var peak = Math.Max(left.Max(v => Math.Abs(v)), right.Max(v => Math.Abs(v)));
        var gain = .58 / Math.Max(.001, peak);
        using var writer = new BinaryWriter(destination, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(36 + count * 4); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(SampleRate);
        writer.Write(SampleRate * 4); writer.Write((short)4); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(count * 4);
        for (var i = 0; i < count; i++)
        {
            if (i % SampleRate == 0) cancellationToken.ThrowIfCancellationRequested();
            var fade = Math.Min(1, Math.Min(i, count - 1 - i) / 240.0);
            writer.Write((short)Math.Round(left[i] * gain * fade * short.MaxValue));
            writer.Write((short)Math.Round(right[i] * gain * fade * short.MaxValue));
        }

        void Add(int midi, double start, double length, double amplitude, double pan, int instrument)
        {
            var hz = 440 * Math.Pow(2, (midi - 69) / 12.0);
            var first = (int)(start * SampleRate);
            var samples = (int)(length * SampleRate);
            var l = amplitude * Math.Sqrt((1 - pan) / 2);
            var r = amplitude * Math.Sqrt((1 + pan) / 2);
            for (var i = 0; i < samples; i++)
            {
                var t = i / (double)SampleRate;
                var phase = 2 * Math.PI * hz * t;
                var envelope = instrument == 0
                    ? Math.Min(1, t / .18) * Math.Pow(Math.Max(0, 1 - t / length), 1.5)
                    : Math.Min(1, t / .008) * Math.Exp(-t * (instrument == 1 ? 3 : 4)) * Math.Min(1, (length - t) / .08);
                var sample = instrument switch
                {
                    0 => Math.Sin(phase) + .16 * Math.Sin(phase * 2.003),
                    1 => Math.Sin(phase) + .3 * Math.Sin(phase * 2) * Math.Exp(-t * 4) + .14 * Math.Sin(phase * 3.99) * Math.Exp(-t * 8),
                    _ => Math.Sin(phase) + .2 * Math.Sin(phase * 2)
                };
                var at = (first + i) % count;
                left[at] += (float)(sample * envelope * l);
                right[at] += (float)(sample * envelope * r);
            }
        }

        void Drum(double start, int kind, int seed)
        {
            var random = new Random(7109 + seed);
            var first = (int)(start * SampleRate);
            var samples = (int)(SampleRate * (kind == 0 ? .22 : .11));
            double previous = 0;
            for (var i = 0; i < samples; i++)
            {
                var t = i / (double)SampleRate;
                var noise = random.NextDouble() * 2 - 1;
                var high = noise - previous;
                previous = noise;
                var sample = kind == 0 ? Math.Sin(2 * Math.PI * (48 * t + 3 * (1 - Math.Exp(-t * 30)))) * Math.Exp(-t * 22) * .09
                    : high * Math.Exp(-t * (kind == 1 ? 42 : 95)) * (kind == 1 ? .025 : .012);
                var at = (first + i) % count;
                left[at] += (float)(sample * (kind == 2 ? .7 : 1));
                right[at] += (float)sample;
            }
        }
    }
}
