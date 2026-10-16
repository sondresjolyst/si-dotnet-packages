namespace SjolystInnovation.AppKit.Storage.Images;

/// <summary>
/// Coordinates the lazy rendition backfill for images uploaded before renditions existed. It keeps one lock
/// per image while a request uses it, and it remembers the images that would not decode, so a broken file
/// is not decoded again on every request.
/// </summary>
internal sealed class VariantBackfill
{
    /// <summary>The most failed images remembered. The oldest is forgotten first.</summary>
    internal const int MaxRemembered = 1024;

    private readonly Dictionary<string, Gate> _gates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly Queue<string> _failedOrder = new();

    /// <summary>The number of locks held or waited for. The lock for an image goes away when no request uses it.</summary>
    internal int LockCount
    {
        get
        {
            lock (_gates)
                return _gates.Count;
        }
    }

    /// <summary>Checks whether an image failed to decode before.</summary>
    public bool HasFailed(string imageId)
    {
        lock (_failed)
            return _failed.Contains(imageId);
    }

    /// <summary>Remembers that an image failed to decode.</summary>
    public void RememberFailure(string imageId)
    {
        lock (_failed)
        {
            if (!_failed.Add(imageId))
                return;
            _failedOrder.Enqueue(imageId);
            if (_failedOrder.Count > MaxRemembered)
                _failed.Remove(_failedOrder.Dequeue());
        }
    }

    /// <summary>Waits for the image's lock. Dispose the result to release it.</summary>
    public async Task<IDisposable> LockAsync(string imageId, CancellationToken cancellationToken)
    {
        Gate gate;
        lock (_gates)
        {
            if (!_gates.TryGetValue(imageId, out gate!))
            {
                gate = new Gate();
                _gates[imageId] = gate;
            }
            gate.Users++;
        }
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(imageId, gate, entered: false);
            throw;
        }
        return new Releaser(this, imageId, gate);
    }

    private void Leave(string imageId, Gate gate, bool entered)
    {
        lock (_gates)
        {
            if (entered)
                gate.Semaphore.Release();
            if (--gate.Users == 0)
            {
                _gates.Remove(imageId);
                gate.Semaphore.Dispose();
            }
        }
    }

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class Releaser(VariantBackfill owner, string imageId, Gate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Leave(imageId, gate, entered: true);
        }
    }
}
