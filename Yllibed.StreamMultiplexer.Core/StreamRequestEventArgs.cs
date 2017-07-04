using System;
using System.IO;
using System.Threading;

namespace Yllibed.StreamMultiplexer.Core
{
	public class StreamRequestEventArgs : EventArgs
	{
		private int _handled = 0;
		public bool Handled => _handled != 0;

		public bool StreamCreated { get; private set; }

		private readonly Func<Stream> _streamFactory;

		/// <summary>
		/// The name of the requested thread.
		/// </summary>
		/// <remarks>
		/// This is an application-specific name.
		/// </remarks>
		public string Name { get; }

		public StreamRequestEventArgs(string name, Func<Stream> streamFactory)
		{
			_streamFactory = streamFactory;
			Name = name;
		}

		/// <summary>
		/// Get the stream associated with this request.
		/// </summary>
		/// <remarks>
		/// The first requester will get it. Any others will be denied.
		/// THIS METHOD IS THREAD-SAFE.
		/// </remarks>
		public bool GetStream(out Stream stream)
		{
			var gotIt = Interlocked.CompareExchange(ref _handled, 1, 0) == 0;
			stream = gotIt ? _streamFactory() : null;
			StreamCreated = StreamCreated || stream != null;
			return stream != null;
		}
	}
}