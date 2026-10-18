using Blackbox.Domain;
using LibVLCSharp.Shared;

namespace Blackbox.App;

public sealed class PlaybackRuntime : IAsyncDisposable
{
    private readonly Lazy<Task<LibVLC>> _runtime = new(() => Task.Run(() =>
    {
        Core.Initialize();
        return new LibVLC("--quiet", "--no-video-title-show", "--no-snapshot-preview");
    }));
    private readonly List<Task> _releases = [];

    public Task<LibVLC> GetAsync() => _runtime.Value;

    public Task ReleaseAsync(MediaPlayer player, Media? media, IDisposable lease,
        IDisposable? input = null, IDisposable? stream = null)
    {
        var task = Task.Run(() =>
        {
            try { player.Stop(); }
            finally
            {
                try { player.Dispose(); }
                finally
                {
                    try { media?.Dispose(); }
                    finally
                    {
                        try { input?.Dispose(); }
                        finally
                        {
                            try { stream?.Dispose(); }
                            finally { lease.Dispose(); }
                        }
                    }
                }
            }
        });
        lock (_releases)
        {
            _releases.RemoveAll(static task => task.IsCompletedSuccessfully);
            _releases.Add(task);
        }
        return task;
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_releases) tasks = _releases.ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        finally
        {
            if (_runtime.IsValueCreated)
            {
                var runtime = await _runtime.Value.ConfigureAwait(false);
                await Task.Run(runtime.Dispose).ConfigureAwait(false);
            }
        }
    }
}
