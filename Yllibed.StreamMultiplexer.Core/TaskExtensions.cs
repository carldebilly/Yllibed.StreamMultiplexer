using System.Threading;
using System.Threading.Tasks;

namespace Yllibed.StreamMultiplexer.Core
{
	internal static class TaskExtensions
	{
		internal static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken ct)
		{
			if (!ct.CanBeCanceled)
			{
				return await task;
			}

			var tcs = new TaskCompletionSource<T>();

			using (ct.Register(() => tcs.TrySetCanceled(ct)))
			{
				var firstTaskToComplete = await Task.WhenAny(task, tcs.Task);
				return await firstTaskToComplete;
			}
		}
	}
}