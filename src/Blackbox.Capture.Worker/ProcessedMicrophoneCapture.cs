using System.Buffers;
using System.Threading.Channels;
using System.IO.Pipes;
using Blackbox.Capture;
using Blackbox.Domain;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Blackbox.Capture.Worker;

internal sealed class ProcessedMicrophoneCapture : IAsyncDisposable
{
    private readonly Stream output;
    private readonly Channel<float[]> frames = Channel.CreateBounded<float[]>(new BoundedChannelOptions(128)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true
    });
    private readonly SemaphoreSlim dataReady = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object processorGate = new();
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly BufferedWaveProvider inputBuffer;
    private readonly ISampleProvider sampleProvider;
    private readonly BroadcastMicrophoneProcessor processor;
    private readonly Task processingTask;
    private readonly Task writerTask;
    private readonly TaskCompletionSource captureStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? failure;
    private long packetCount;
    private long droppedFrames;
    private bool paused;
    private bool muted;
    private bool started;
    private bool stopped;

    public ProcessedMicrophoneCapture(
        Stream output,
        string? deviceId,
        MicrophoneProcessingSettings settings,
        bool muted = false)
    {
        this.output = output;
        this.muted = muted;
        device = OpenDevice(enumerator, deviceId);
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
        processor = new BroadcastMicrophoneProcessor(settings);
        capture = new WasapiCapture(device);
        inputBuffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };
        ISampleProvider samples = inputBuffer.ToSampleProvider();
        samples = new MonoSampleProvider(samples);
        sampleProvider = samples.WaveFormat.SampleRate == BroadcastMicrophoneProcessor.SampleRate
            ? samples
            : new WdlResamplingSampleProvider(samples, BroadcastMicrophoneProcessor.SampleRate);
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
        processingTask = Task.Run(ProcessAsync);
        writerTask = Task.Run(WriteAsync);
    }

    public string DeviceId { get; }
    public string DeviceName { get; }
    public bool IsEnabled => !muted;
    public long PacketCount => Interlocked.Read(ref packetCount);
    public long DroppedFrames => Interlocked.Read(ref droppedFrames);
    public string? Failure => Volatile.Read(ref failure);
    public MicrophoneProcessingSettings Settings => processor.Settings;
    public string Preset => processor.Settings.Preset.ToString();
    public bool ProcessingEnabled => processor.Settings.ProcessingEnabled;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(stopped, this);
        capture.StartRecording();
        started = true;
    }

    public void Update(MicrophoneProcessingSettings settings, bool isMuted)
    {
        ObjectDisposedException.ThrowIf(stopped, this);
        lock (processorGate)
        {
            processor.Update(settings);
            muted = isMuted;
        }
    }

    public void SetPaused(bool value)
    {
        paused = value;
        if (value)
        {
            inputBuffer.ClearBuffer();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs data)
    {
        if (paused || stopped || data.BytesRecorded <= 0)
        {
            return;
        }

        try
        {
            inputBuffer.AddSamples(data.Buffer, 0, data.BytesRecorded);
            if (dataReady.CurrentCount == 0)
            {
                dataReady.Release();
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs result)
    {
        if (result.Exception is { } error)
        {
            Fail(error);
        }

        captureStopped.TrySetResult();
    }

    private async Task ProcessAsync()
    {
        var readBuffer = new float[BroadcastMicrophoneProcessor.FrameSamples * 2];
        var frame = ArrayPool<float>.Shared.Rent(BroadcastMicrophoneProcessor.FrameSamples);
        var frameOffset = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await dataReady.WaitAsync(lifetime.Token).ConfigureAwait(false);
                while (true)
                {
                    var read = sampleProvider.Read(readBuffer, 0, readBuffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    var sourceOffset = 0;
                    while (sourceOffset < read)
                    {
                        var count = Math.Min(BroadcastMicrophoneProcessor.FrameSamples - frameOffset, read - sourceOffset);
                        readBuffer.AsSpan(sourceOffset, count).CopyTo(frame.AsSpan(frameOffset));
                        sourceOffset += count;
                        frameOffset += count;
                        if (frameOffset != BroadcastMicrophoneProcessor.FrameSamples)
                        {
                            continue;
                        }

                        var outputFrame = frame;
                        lock (processorGate)
                        {
                            if (muted)
                            {
                                outputFrame.AsSpan(0, BroadcastMicrophoneProcessor.FrameSamples).Clear();
                            }
                            else
                            {
                                processor.Process(outputFrame.AsSpan(0, BroadcastMicrophoneProcessor.FrameSamples));
                            }
                        }

                        if (!frames.Writer.TryWrite(outputFrame))
                        {
                            Interlocked.Increment(ref droppedFrames);
                            ArrayPool<float>.Shared.Return(outputFrame);
                        }

                        frame = ArrayPool<float>.Shared.Rent(BroadcastMicrophoneProcessor.FrameSamples);
                        frameOffset = 0;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(frame);
            frames.Writer.TryComplete();
        }
    }

    private async Task WriteAsync()
    {
        var bytes = new byte[BroadcastMicrophoneProcessor.FrameSamples * sizeof(float)];
        try
        {
            if (output is NamedPipeServerStream namedPipe && !namedPipe.IsConnected)
            {
                await namedPipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
            }

            await foreach (var frame in frames.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    Buffer.BlockCopy(frame, 0, bytes, 0, bytes.Length);
                    await output.WriteAsync(bytes, lifetime.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref packetCount);
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(frame);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            while (frames.Reader.TryRead(out var frame))
            {
                ArrayPool<float>.Shared.Return(frame);
            }
        }
    }

    private void Fail(Exception error)
    {
        Interlocked.CompareExchange(ref failure, "Microphone processing failed: " + error.GetBaseException().Message, null);
        lifetime.Cancel();
        captureStopped.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (stopped)
        {
            return;
        }

        stopped = true;
        capture.DataAvailable -= OnDataAvailable;
        try
        {
            if (started)
            {
                capture.StopRecording();
                await captureStopped.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            Interlocked.CompareExchange(ref failure, "Microphone capture did not stop within 3 seconds.", null);
        }
        finally
        {
            capture.RecordingStopped -= OnRecordingStopped;
            lifetime.Cancel();
            if (dataReady.CurrentCount == 0)
            {
                dataReady.Release();
            }

            frames.Writer.TryComplete();
        }

        try
        {
            await Task.WhenAll(processingTask, writerTask).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Interlocked.CompareExchange(ref failure, "Microphone audio pipeline did not stop within 3 seconds.", null);
        }
        finally
        {
            output.Dispose();
            processor.Dispose();
            capture.Dispose();
            device.Dispose();
            enumerator.Dispose();
            dataReady.Dispose();
            lifetime.Dispose();
        }
    }

    private static MMDevice OpenDevice(MMDeviceEnumerator deviceEnumerator, string? deviceId) =>
        string.IsNullOrWhiteSpace(deviceId) || deviceId.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : deviceEnumerator.GetDevice(deviceId);

    private sealed class MonoSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private readonly float[] sourceBuffer = new float[BroadcastMicrophoneProcessor.FrameSamples * 8];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = source.WaveFormat.Channels;
            var sourceSamples = Math.Min(sourceBuffer.Length, count * channels);
            sourceSamples -= sourceSamples % channels;
            var read = source.Read(sourceBuffer, 0, sourceSamples);
            var framesRead = read / channels;
            for (var frameIndex = 0; frameIndex < framesRead; frameIndex++)
            {
                double sum = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += sourceBuffer[(frameIndex * channels) + channel];
                }

                buffer[offset + frameIndex] = (float)(sum / channels);
            }

            return framesRead;
        }
    }
}
