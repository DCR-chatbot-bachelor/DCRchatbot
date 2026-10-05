namespace DcrChatbot.Infrastructure.Session;

internal sealed class SessionLockManager
{
    private readonly Dictionary<string, LockEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object syncRoot = new();

    public async Task<IDisposable> AcquireAsync(string sessionId, CancellationToken cancellationToken)
    {
        LockEntry entry;
        lock (syncRoot)
        {
            if (!entries.TryGetValue(sessionId, out var existingEntry))
            {
                existingEntry = new LockEntry();
                entries[sessionId] = existingEntry;
            }

            entry = existingEntry;
            entry.ReferenceCount++;
        }

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
        var shouldDispose = false;
        lock (syncRoot)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0)
            {
                entries.Remove(sessionId);
                shouldDispose = true;
            }
        }

        if (shouldDispose)
        {
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