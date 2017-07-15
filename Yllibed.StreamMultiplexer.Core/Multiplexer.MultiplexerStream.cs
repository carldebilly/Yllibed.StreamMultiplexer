using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Yllibed.StreamMultiplexer.Core
{
	partial class Multiplexer
	{
		public sealed class MultiplexerStream : Stream
		{
			private const ushort PacketPayloadSize = 1402;

			private readonly Multiplexer _owner;
			private readonly ushort _streamId;

			private bool _isClosed;

			// Writing stuff
			private readonly byte[] _sendingBuffer = new byte[PacketPayloadSize];
			private readonly SemaphoreSlim _writingSemaphore = new SemaphoreSlim(1, 1);
			private readonly SemaphoreSlim _sendingWindow;
			private ushort _sendingBufferPointer = 0;

			// Reading stuff
			private ImmutableQueue<byte[]> _receivedBuffers = ImmutableQueue<byte[]>.Empty;
			private readonly SemaphoreSlim _readingSemaphore = new SemaphoreSlim(1, 1);
			private byte[] _readingBuffer = null;
			private ushort _readingBufferPointer = 0;
			private readonly ManualResetEventSlim _receivedData = new ManualResetEventSlim(false);

			// In the Stream base class, the "async" version of .Read(), .Write() & .Flush()
			// are calling the blocking version using the TaskScheduler.
			//
			// This implementation is doing the opposite: the "sync" version are calling
			// the async version in a blocking manner.
			internal MultiplexerStream(Multiplexer owner, ushort streamId, ushort remoteWindowSize)
			{
				_owner = owner;
				_streamId = streamId;
				_sendingWindow = new SemaphoreSlim(remoteWindowSize, remoteWindowSize);
			}

			public override long Seek(long offset, SeekOrigin origin)
			{
				throw new NotSupportedException();
			}

			public override void SetLength(long value) => throw new InvalidOperationException();

			public override int Read(byte[] buffer, int offset, int count)
			{
				var t = ReadAsync(buffer, offset, count, CancellationToken.None);
				t.Wait();
				return t.Result;
			}

			public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
			{
				if (buffer == null)
				{
					throw new ArgumentNullException(nameof(buffer));
				}
				if (buffer.GetLowerBound(0) != 0 || buffer.Length < offset + count)
				{
					throw new ArgumentException(nameof(buffer));
				}
				if (offset < 0)
				{
					throw new ArgumentOutOfRangeException(nameof(offset));
				}
				if (count <= 0)
				{
					throw new ArgumentOutOfRangeException(nameof(count));
				}

				await _readingSemaphore.WaitAsync(ct); // Operates the reading operations under an exclusive concurrency

				var readBytes = 0;

				try
				{
					while (true)
					{
						if (count <= 0 && readBytes > 0)
						{
							if (_readingBufferPointer == _readingBuffer.Length)
							{
								// We're finish with current buffer: dequeue next immediatly & send DACK
								await DequeueReceivedBuffer(ct, waitForBuffer: false);
							}

							return readBytes;
						}

						var availableInReadingBuffer = (_readingBuffer?.Length ?? 0) - _readingBufferPointer;
						if (availableInReadingBuffer <= 0)
						{
							if (!await DequeueReceivedBuffer(ct, waitForBuffer: readBytes == 0))
							{
								return readBytes; // Requester got something or 0 if stream is closed
							}
							continue;
						}

						// Determine if we need all the _readingBuffer or not
						var bytesToCopy = availableInReadingBuffer >= count
							? count
							: availableInReadingBuffer;

						Array.Copy(_readingBuffer, _readingBufferPointer, buffer, offset, bytesToCopy);

						// Increment the count of copied bytes
						readBytes += bytesToCopy;
						// Advance the reading pointer
						_readingBufferPointer += (ushort)bytesToCopy;
						// Reduce count for next read (if any)
						count -= bytesToCopy;
						// Advance the next read offset (if occurres)
						offset += bytesToCopy;
					}
				}
				finally
				{
					_readingSemaphore.Release();
				}
			}

			private async Task<bool> DequeueReceivedBuffer(CancellationToken ct, bool waitForBuffer)
			{
				while (!ct.IsCancellationRequested)
				{
					var capture = _receivedBuffers;

					if (capture.IsEmpty)
					{
						// nothing yet available ?

						if (!waitForBuffer || _isClosed)
						{
							break;
						}

						// wait for something to arrive on this stream
						await _receivedData.WaitAsync(ct);
						_receivedData.Reset();

						// restart this loop
						continue;
					}

					var updated = capture.Dequeue(out var buffer);
					if (Interlocked.CompareExchange(ref _receivedBuffers, updated, capture) == capture)
					{
						_readingBuffer = buffer;
						_readingBufferPointer = 0;

						// Send a DACK for this buffer to other peer
						await _owner.SendDACK(_streamId);
						return true;
					}
				}
				return false;
			}

			internal void OnReceivedBuffer(byte[] buffer)
			{
				while (true) // Optimistic concurrency pattern
				{
					var capture = _receivedBuffers;
					var updated = capture.Enqueue(buffer);
					if (Interlocked.CompareExchange(ref _receivedBuffers, updated, capture) == capture)
					{
						break; // added to _receivedBuffers
					}
				}

				// Unblock any waiting .Read() operations
				_receivedData.Set();
			}

			internal void OnReceivedClose()
			{
				_isClosed = true;
				Dispose();
			}

			public override void Write(byte[] buffer, int offset, int count)
			{
				WriteAsync(buffer, offset, count, CancellationToken.None).Wait();
			}

			public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
			{
				if (buffer == null)
				{
					throw new ArgumentNullException(nameof(buffer));
				}
				if (buffer.GetLowerBound(0) != 0 || buffer.Length < offset + count)
				{
					throw new ArgumentException(nameof(buffer));
				}
				if (offset < 0)
				{
					throw new ArgumentOutOfRangeException(nameof(offset));
				}
				if (count < 0)
				{
					throw new ArgumentOutOfRangeException(nameof(count));
				}

				await _writingSemaphore.WaitAsync(ct); // Operates the writing operations under an exclusive concurrency

				try
				{
					if (count == 0)
					{
						return; // nothing to do
					}

					var bufferPointer = offset;
					var endPointer = offset + count;

					if (endPointer > buffer.Length || count < 0 || offset < 0 || offset > buffer.Length - 1)
					{
						throw new ArgumentOutOfRangeException(nameof(count));
					}

					while (bufferPointer < endPointer && !ct.IsCancellationRequested)
					{
						// Copy buffer to sending packet
						var remainingSendingBufferSpace = (ushort)(PacketPayloadSize - _sendingBufferPointer);
						if (remainingSendingBufferSpace == 0)
						{
							await FlushAsyncInternal(ct);
							continue; // recalculate remainingSendingBufferSpace
						}
						if (count <= 0)
						{
							break; // finished sending the buffer
						}

						if (remainingSendingBufferSpace < count)
						{
							Array.Copy(buffer, bufferPointer, _sendingBuffer, _sendingBufferPointer, remainingSendingBufferSpace);
							bufferPointer += remainingSendingBufferSpace;
							count -= remainingSendingBufferSpace;
							_sendingBufferPointer += remainingSendingBufferSpace;
						}
						else
						{
							Array.Copy(buffer, bufferPointer, _sendingBuffer, _sendingBufferPointer, count);
							_sendingBufferPointer += (ushort)count;
							bufferPointer += count;
							count = 0;
						}
					}
				}
				finally
				{
					_writingSemaphore.Release();
				}
			}

			public override void Flush()
			{
				FlushAsync(CancellationToken.None).Wait();
			}

			public override async Task FlushAsync(CancellationToken ct)
			{
				await _writingSemaphore.WaitAsync(ct); // Operates the writing operations under an exclusive concurrency

				try
				{
					await FlushAsyncInternal(ct);
				}
				finally
				{
					_writingSemaphore.Release();
				}
			}

			private async Task FlushAsyncInternal(CancellationToken ct)
			{
				// Check if there's something to send
				if (_sendingBufferPointer == 0)
				{
					return; // nothing to do
				}

				// Wait until sending window is clear to send
				await _sendingWindow.WaitAsync(ct);

				// Send waiting packet
				await _owner.SendDATA(_streamId, _sendingBuffer, _sendingBufferPointer);
				_sendingBufferPointer = 0;
			}

			internal void ReceivedDACK()
			{
				// Reopen sending window by one
				_sendingWindow.Release();
			}

			public override bool CanRead { get; } = true;
			public override bool CanSeek { get; } = false;
			public override bool CanWrite { get; } = true;
			public override long Length => throw new InvalidOperationException();
			public bool DataAvailable => _readingBufferPointer < (_readingBuffer?.Length ?? 0) || !_receivedBuffers.IsEmpty;

			public override long Position
			{
				get => throw new InvalidOperationException();
				set => throw new InvalidOperationException();
			}

			protected override void Dispose(bool disposing)
			{
				base.Dispose(disposing);

				if (!_isClosed)
				{
					Flush();

					_isClosed = true;

					// Send closing stream to other peer
					_owner.SendFIN(_streamId, MultiplexerTerminationType.NORMAL);

					// unblock any waiting .Read() operation
					_receivedData.Set();
				}

				// Remove the streams of the multiplexer list
				while (true)
				{
					var capture = _owner._streams;
					if (!capture.ContainsKey(_streamId))
					{
						break;
					}
					var updated = capture.Remove(_streamId);
					if(Interlocked.CompareExchange(ref _owner._streams, updated, capture) == capture)
					{
						break;
					}
				}
			}
		}

	}
}
