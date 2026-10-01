namespace ClaudeUsageTracker.App.Storage;

/// <summary>
/// The one app-wide asynchronous lock around every credential change: refresh, import, deletion,
/// takeover and write-back, across all profiles (plan.md section 2). It is held through a refresh request
/// and its persistence; ordinary usage reads do not take it. Claude Code and other external writers do
/// not honour it, so file and process checks remain.
/// </summary>
public sealed class CredentialLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Waits for the lock; dispose the result to release it.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellation)
    {
        await _semaphore.WaitAsync(cancellation).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    /// <summary>Takes the lock from synchronous code, such as a settings action on the UI thread.</summary>
    public IDisposable Acquire()
    {
        _semaphore.Wait();
        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
