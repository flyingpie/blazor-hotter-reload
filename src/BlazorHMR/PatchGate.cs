namespace BlazorHMR;

/// <summary>
/// Serializes the two ways a change can be applied: a full swap and an in-place patch must never touch the runtime at the
/// same time, because both replace or mutate the current generation and re-render the component tree.
/// </summary>
internal sealed class PatchGate : IDisposable
{
	private readonly SemaphoreSlim _semaphore = new(1, 1);

	/// <summary>
	/// Waits until no other patch is being applied. Dispose the lease to let the next one in.
	/// </summary>
	public async Task<IDisposable> EnterAsync()
	{
		await _semaphore.WaitAsync().ConfigureAwait(true);
		return new Lease(_semaphore);
	}

	public void Dispose() => _semaphore.Dispose();

	private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
	{
		public void Dispose() => semaphore.Release();
	}
}
