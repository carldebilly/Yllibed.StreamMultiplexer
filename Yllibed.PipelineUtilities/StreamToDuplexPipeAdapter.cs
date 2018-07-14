using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Yllibed.PipelineUtilities
{
	public class StreamToDuplexPipeAdapter : IDisposable, IDuplexPipe
	{
		private readonly Stream _sourceStream;
		private readonly PipeOptions _pipeOptions;
		private readonly Pipe _readingPipe;
		private readonly Pipe _writingPipe;
		private readonly CancellationTokenSource _cts = new CancellationTokenSource();

		private readonly Task _process;

		public PipeReader Input => _readingPipe.Reader;

		public PipeWriter Output => _writingPipe.Writer;

		public StreamToDuplexPipeAdapter(Stream sourceStream, PipeOptions pipeOptions = null)
		{
			_sourceStream = sourceStream;
			_pipeOptions = pipeOptions ?? PipeOptions.Default;

			if (_sourceStream.CanRead)
			{
				_readingPipe = new Pipe(_pipeOptions);
			}

			if (_sourceStream.CanWrite)
			{
				_writingPipe = new Pipe(_pipeOptions);
			}

			_process = Process();
		}

		private async Task Process()
		{
			var reading = ReadingProcess();
			var writing = WritingProcess();

			await reading;
			await writing;

			Dispose();
		}

		private async ValueTask ReadingProcess()
		{
			if (_readingPipe == null)
			{
				return;
			}

			var writer = _readingPipe.Writer;
			var ct = _cts.Token;

			while (!ct.IsCancellationRequested)
			{
				var memory = writer.GetMemory(1);
#if NETCOREAPP2_1
				var bytesRead = await _sourceStream.ReadAsync(memory, ct);
#else
				var buffer = memory.AsArray();
				var bytesRead = await _sourceStream.ReadAsync(buffer.Array, buffer.Offset, buffer.Count, ct);
#endif
				if (bytesRead <= 0)
				{
					break;
				}
				writer.Advance(bytesRead);
				var flushResult = await writer.FlushAsync(ct);
				if (flushResult.IsCompleted || flushResult.IsCanceled)
				{
					break;
				}
			}
		}

		private async ValueTask WritingProcess()
		{
			if (_writingPipe == null)
			{
				return;
			}

			var reader = _writingPipe.Reader;
			var ct = _cts.Token;

			while (!ct.IsCancellationRequested)
			{
				var readResult = await reader.ReadAsync(ct);

				var readBuffer = readResult.Buffer;
				if (!readBuffer.IsEmpty)
				{
#if NETCOREAPP2_1
					var firstSegment = readBuffer.First;
					await _sourceStream.WriteAsync(firstSegment, ct);
					reader.AdvanceTo(readBuffer.GetPosition(firstSegment.Length));
#else
					var array = readBuffer.First.AsArray();
					await _sourceStream.WriteAsync(array.Array, array.Offset, array.Count, ct).ConfigureAwait(_pipeOptions.UseSynchronizationContext);
					reader.AdvanceTo(readBuffer.GetPosition(array.Count));
#endif
				}
				else if (readResult.IsCompleted)
				{
					break;
				}

				await _sourceStream.FlushAsync(ct);
			}
		}

		public void Dispose()
		{
			_cts.Cancel();
			_readingPipe.Writer.Complete();
			_writingPipe.Reader.Complete();
			_sourceStream.Flush();
			_sourceStream.Dispose();
		}
	}
}
