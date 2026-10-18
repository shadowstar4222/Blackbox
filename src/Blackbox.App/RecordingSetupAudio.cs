using NAudio.Wave;

namespace Blackbox.App;

internal sealed class RecordingSetupAudio : IDisposable
{
    private readonly WaveFileReader _reader;
    private readonly WasapiOut _output;
    private readonly VolumeWaveProvider16 _volume;

    internal RecordingSetupAudio(string path)
    {
        _reader = new WaveFileReader(path);
        try
        {
            _volume = new VolumeWaveProvider16(new LoopingSetupWave(_reader)) { Volume = .3f };
            _output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
            try { _output.Init(_volume); }
            catch { _output.Dispose(); throw; }
        }
        catch { _reader.Dispose(); throw; }
    }

    internal void Play() => _output.Play();
    internal void Pause() => _output.Pause();
    internal void SetVolume(double value) => _volume.Volume = (float)Math.Clamp(value, 0, 1);
    public void Dispose() { _output.Dispose(); _reader.Dispose(); }
}

internal sealed class LoopingSetupWave(WaveStream source) : IWaveProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = source.Read(buffer, offset + total, count - total);
            total += read;
            if (read == 0)
            {
                if (source.Position == 0) break;
                source.Position = 0;
            }
        }
        return total;
    }
}
