using System;
using System.Threading;
using System.Threading.Tasks;

namespace Yllibed.StreamMultiplexer.Core
{
	/// <summary>
	/// Awaitable ManualResetEvent 
	/// </summary>
	/// <remarks>
	/// There's no good way to await for a ManualResetEvent using async/await, except
	/// in "full .NET framework". This class is using instances of TaskCompletionSource
	/// to create a similar synchronization mecanism.
	/// </remarks>
	internal class ManualResetEventSlim
	{
		private TaskCompletionSource<object> _tcs;

		internal ManualResetEventSlim(bool initialState)
		{
			if (initialState)
			{
				Set();
			}
		}

		public void Reset()
		{
			while (true)
			{
				var tcs = _tcs;
				if (tcs == null || !tcs.Task.IsCompleted)
				{
					return; // that's perfect, nothing to do
				}

				// If the current TCS is completed, need to replace it
				// with a new, non-completed one.
				if (Interlocked.CompareExchange(ref _tcs, null, tcs) != tcs)
				{
					// concurrency collision!  Retrying...
					// > we don't want to "lose" any previous non-completed TCS or
					// > we'll have deadlocks!
					continue; 
				}

				return; // successfully removed old TCS
			}
		}

		public void Set()
		{
			GetTcs().TrySetResult(null);
		}

		public Task WaitAsync(CancellationToken ct)
		{
			return GetTcs().Task.WaitAsync(ct);
		}

		private TaskCompletionSource<object> GetTcs()
		{
			var tcs = _tcs;
			if (tcs == null)
			{
				// Non-existing TCS, so we create a new one
				tcs = new TaskCompletionSource<object>();

				// Concurrency safety (tcs will have the right version, no matter if we successfully created it locally or not)
				tcs = Interlocked.CompareExchange(ref _tcs, tcs, null) ?? tcs;
			}
			return tcs;
		}
	}
}
