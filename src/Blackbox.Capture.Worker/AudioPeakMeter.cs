namespace Blackbox.Capture.Worker;

// Reads the encoder's existing gain callback; PCM packet preview stays disabled.
internal sealed class AudioPeakMeter
{
    private readonly object gate = new();
    private double peak;
    private long windowTick;
    private long packetTick;
    private long packets;

    public void Add(double amplitude, long now)
    {
        if (!double.IsFinite(amplitude) || amplitude < 0) return;
        lock (gate)
        {
            if (packets == 0 || now - windowTick >= 250)
            {
                peak = 0;
                windowTick = now;
            }
            peak = Math.Max(peak, Math.Min(1, amplitude));
            packetTick = now;
            packets++;
        }
    }

    public (double? PeakDb, long Packets) Read(long now)
    {
        lock (gate)
            return (packets == 0 || now - packetTick > 1000 ? null :
                Math.Clamp(20 * Math.Log10(Math.Max(peak, 0.0000158489)), -96, 0), packets);
    }
}
