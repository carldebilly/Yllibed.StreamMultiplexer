using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Yllibed.StreamMultiplexer.Core
{
	public interface IMultiplexer
	{
		/// <summary>
		/// Received a stream request.
		/// </summary>
		/// <remarks>
		/// To accept it, you need to call the `GetStream()` method from
		/// the _EventArgs_ of the event. The first event handler to request
		/// it will get it.
		/// If none request it, the requested stream will be rejected.
		/// </remarks>
		event EventHandler<StreamRequestEventArgs> RequestedStream;

		/// <summary>
		/// Launch the multiplexing process.
		/// </summary>
		/// <remarks>
		/// This is a two-steps process to let application register to the `RequestedStream`
		/// handler before launching the multiplexing process.
		/// </remarks>
		void Start();

		/// <summary>
		/// Request a new multiplexed stream using a name.
		/// </summary>
		/// <param name="ct"></param>
		/// <param name="name">Name of the string to request - will be interpreted by the other side as-is.</param>
		/// <returns>Stream if successfully accepted by other end. Null means not accepted.</returns>
		Task<Stream> RequestStream(CancellationToken ct, string name);
	}
}
