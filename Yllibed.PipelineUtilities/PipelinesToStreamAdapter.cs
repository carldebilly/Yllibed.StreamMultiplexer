using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Yllibed.PipelineUtilities
{
	public class PipelinesToStreamAdapter : Stream
	{
		public PipeReader PipeReader { get; }
		public PipeWriter PipeWriter { get; }

		private readonly PipeOptions _pipeOptions;

		public PipelinesToStreamAdapter(PipeReader pipeReader = null, PipeWriter pipeWriter = null, PipeOptions pipeOptions = null)
		{
			PipeReader = pipeReader;
			PipeWriter = pipeWriter;
			_pipeOptions = pipeOptions ?? PipeOptions.Default;
		}

		public PipelinesToStreamAdapter(IDuplexPipe pipes, PipeOptions pipeOptions = null)
		{
			PipeReader = pipes.Input;
			PipeWriter = pipes.Output;
			_pipeOptions = pipeOptions ?? PipeOptions.Default;
		}

		public override bool CanRead => PipeReader != null;

		public override bool CanSeek => false;

		public override bool CanTimeout => false;

		public override bool CanWrite => PipeWriter != null;

		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override int ReadTimeout
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override int WriteTimeout
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

#if !NETSTANDARD1_4
		public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
		{
			var task = ReadAsync(buffer, offset, count, CancellationToken.None);

			var tcs = new TaskCompletionSource<int>(state);

			void CompleteTask(Task<int> t)
			{
				if (t.IsFaulted)
				{
					tcs.TrySetException(t.Exception.InnerException);
				}
				else if (t.IsCanceled)
				{
					tcs.TrySetCanceled();
				}
				else
				{
					tcs.TrySetResult(t.Result);
				}

				callback?.Invoke(tcs.Task);
			}

			if (task.IsCompleted)
			{
				CompleteTask(task);
			}
			else
			{
				task.ContinueWith(CompleteTask);
			}

			return tcs.Task;
		}

		public override int EndRead(IAsyncResult asyncResult)
		{
			return ((Task<int>) asyncResult).Result;
		}

		public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
		{
			var task = WriteAsync(buffer, offset, count, CancellationToken.None);

			var tcs = new TaskCompletionSource<object>(state);

			void CompleteTask(Task t)
			{
				if (t.IsFaulted)
				{
					tcs.TrySetException(t.Exception.InnerException);
				}
				else if (t.IsCanceled)
				{
					tcs.TrySetCanceled();
				}
				else
				{
					tcs.TrySetResult(null);
				}

				callback?.Invoke(tcs.Task);
			}

			if (task.IsCompleted)
			{
				CompleteTask(task);
			}
			else
			{
				task.ContinueWith(CompleteTask);
			}

			return tcs.Task;
		}

		public override void EndWrite(IAsyncResult asyncResult)
		{
			((Task)asyncResult).Wait();
		}

		public override void Close()
		{
			PipeReader.Complete();
			PipeWriter.Complete();
		}
#else
		protected override void Dispose(bool disposing)
		{
			PipeReader.Complete();
			PipeWriter.Complete();

			base.Dispose(disposing);
		}
#endif

		public override void Flush()
		{
			FlushAsync(CancellationToken.None).Wait();
		}

		public override async Task FlushAsync(CancellationToken ct)
		{
			await PipeWriter.FlushAsync(ct);
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			var t = ReadAsync(buffer, offset, count, CancellationToken.None);
			t.Wait();
			return t.Result;
		}

		public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
		{
			var bufferMemory = new Memory<byte>(buffer, offset, count);

			return await ReadAsync(bufferMemory, ct);
		}

#if NETCOREAPP2_1
		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = new CancellationToken())
#else
		public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = new CancellationToken())
#endif
		{
			if (buffer.IsEmpty)
			{
				return 0; // fast path when nothing to do
			}

			var bufferLength = buffer.Length;
			while (true)
			{
				var result = await PipeReader.ReadAsync(ct);

				var resultBuffer = result.Buffer;

				if (!resultBuffer.IsEmpty)
				{
					var firstBuffer = resultBuffer.First;
					var firstBufferLength = firstBuffer.Length;
					if (firstBufferLength <= bufferLength)
					{
						firstBuffer.CopyTo(buffer);
						PipeReader.AdvanceTo(resultBuffer.GetPosition(firstBufferLength));
						return firstBufferLength;
					}
					else
					{
						var slice = firstBuffer.Slice(0, bufferLength);
						slice.CopyTo(buffer);
						PipeReader.AdvanceTo(resultBuffer.GetPosition(bufferLength));
						return bufferLength;
					}
				}

				if (result.IsCompleted)
				{
					return 0; // reading pipe finished.
				}
			}
		}

		public override int ReadByte()
		{
			while (true)
			{
				if (!PipeReader.TryRead(out var result))
				{
					async Task WaitForResult()
					{
						result = await PipeReader.ReadAsync(CancellationToken.None);
					}

					WaitForResult().Wait();
				}

				var resultBuffer = result.Buffer;
				if (!resultBuffer.IsEmpty)
				{
					var firstBuffer = resultBuffer.First;
					var firstByte = firstBuffer.Span[0];
					PipeReader.AdvanceTo(resultBuffer.GetPosition(1));
					return firstByte;
				}

				if (result.IsCompleted)
				{
					return -1;
				}
			}
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			var span = new ReadOnlySpan<byte>(buffer);
			Write(span);
		}

#if NETCOREAPP2_1
		public override void Write(ReadOnlySpan<byte> buffer)
#else
		public void Write(ReadOnlySpan<byte> buffer)
#endif
		{
			var offset = 0;
			while (offset < buffer.Length)
			{
				var span = PipeWriter.GetSpan(1);
				if (span.Length < buffer.Length - offset)
				{
					// Span from writer is smaller than buffer remaining
					var toSend = buffer.Slice(offset, span.Length);
					toSend.CopyTo(span);
					PipeWriter.Advance(span.Length);
					offset += toSend.Length;
				}
				else
				{
					// Span is long enough to accomodate remaining buffer
					var toSend = buffer.Slice(offset, buffer.Length - offset);
					toSend.CopyTo(span);
					PipeWriter.Advance(toSend.Length);
					break;
				}
			}
		}

		public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
		{
			await WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), ct);
		}

#if NETCOREAPP2_1
		public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = new CancellationToken())
#else
		public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = new CancellationToken())
#endif
		{
			await PipeWriter.WriteAsync(buffer, ct);
		}

		public override void WriteByte(byte value)
		{
			var span = PipeWriter.GetSpan(1);
			span[0] = value;
			PipeWriter.Advance(1);
		}
	}
}
