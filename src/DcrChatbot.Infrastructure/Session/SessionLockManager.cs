using System.Collections.Concurrent;

namespace DcrChatbot.Infrastructure.Session;

internal sealed class SessionLockManager
{
    private readonly ConcurrentDictionary<string, LockEntry> entries = new();

    public async Task<IDisposable> AcquireAsync(string sessionId, CancellationToken cancellationToken)
    {
        var entry = entries.GetOrAdd(sessionId, _ => new LockEntry());
        Interlocked.Increment(ref entry.ReferenceCount);
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
            return new Lease(this, sessionId, entry);
        }
        catch
        {
            ReleaseReference(sessionId, entry);
            throw;
        }
    }

    private void Release(string sessionId, LockEntry entry)
    {
        entry.Semaphore.Release();
        ReleaseReference(sessionId, entry);
    }

    private void ReleaseReference(string sessionId, LockEntry entry)
    {
        if (Interlocked.Decrement(ref entry.ReferenceCount) == 0)
        {
            entries.TryRemove(new KeyValuePair<string, LockEntry>(sessionId, entry));
            entry.Semaphore.Dispose();
        }
    }

    private sealed class LockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount;
    }

    private sealed class Lease(
        SessionLockManager owner,
        string sessionId,
        LockEntry entry) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                owner.Release(sessionId, entry);
            }
        }
    }
}