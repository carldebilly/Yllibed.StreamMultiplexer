using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Yllibed.StreamMultiplexer.Core;

namespace Yllibed.StreamMultiplexer.Tests
{
	[TestClass]
	public class MultiplexerFixture
	{
		private CancellationToken _ct;
		private CancellationTokenSource _cts;

		[TestInitialize]
		public void Setup()
		{
			_cts = Debugger.IsAttached
				? new CancellationTokenSource()
				: new CancellationTokenSource(TimeSpan.FromSeconds(25));
			_ct = _cts.Token;
		}

		[TestCleanup]
		public void Teardown()
		{
			_cts.Dispose();
		}

		[TestMethod]
		public async Task MultiplexerSanityTest()
		{
			(_, _, var multiplexerA, var multiplexerB, var subStreamA, var subStreamB) = await GetSubStreamsPair();

			var bufferA = new byte[10];
			var bufferB = new byte[10];

			var readATask = subStreamA.ReadAsync(bufferA, 0, 10, _ct);
			for (byte i = 0; i < bufferB.Length; i++)
			{
				bufferB[i] = i;
			}
			await subStreamB.WriteAsync(bufferB, 0, 10, _ct);
			await subStreamB.FlushAsync(_ct);

			var readFromA = await readATask;
			readFromA.Should().Be(10);
			bufferA.Should().BeEquivalentTo(bufferB);

			subStreamB.Dispose();
			multiplexerB.NumberOfActiveStreams.Should().Be(0);

			subStreamA.ReadByte().Should().Be(-1);
			multiplexerA.NumberOfActiveStreams.Should().Be(0);
		}

		[TestMethod]
		public async Task MultiplexerTestNonLatinStreamNames()
		{
			(_, _, var multiplexerA, var multiplexerB, var subStreamA, var subStreamB) = await GetSubStreamsPair();

			const string streamName = "👣👨‍👨‍👧🎂";

			Stream output = null;

			void OnMultiplexerBOnRequestedStream(object snd, StreamRequestEventArgs evt)
			{
				if (evt.Name == streamName)
				{
					evt.GetStream(out output);
				}
			}

			multiplexerB.RequestedStream += OnMultiplexerBOnRequestedStream;

			var s = await multiplexerA.RequestStream(_ct, streamName);

			s.Should().NotBeNull();
			output.Should().NotBeNull();
		}

		[TestMethod]
		public async Task MultiplexerTestRejectedStreamRequests()
		{
			(_, _, var multiplexerA, var multiplexerB, _, _) = await GetSubStreamsPair();

			for (var i = 0; i < 100; i++)
			{
				var stream = await multiplexerA.RequestStream(_ct, "streamA" + 1);
				stream.Should().BeNull();
			}

			for (var i = 0; i < 100; i++)
			{
				var stream = await multiplexerB.RequestStream(_ct, "streamB" + 1);
				stream.Should().BeNull();
			}
		}

		[TestMethod]
		[Ignore]
		public async Task MultiplexerTestNestedMultiplexer()
		{
			(_, _, _, _, var streamA, var streamB) = await GetSubStreamsPair();

			var multiplexerA = new Multiplexer(streamA);
			var multiplexerB = new Multiplexer(streamB);

			multiplexerA.RequestedStream += (snd, evt) => evt.GetStream(out _);

			multiplexerA.Start();
			await Task.Yield();
			multiplexerB.Start();
			await Task.Yield();

			var s = await multiplexerB.RequestStream(_ct, "123");
			s.Should().NotBeNull();
		}

		[TestMethod]
		[Ignore]
		public async Task MultiplexerTestStreamIdReusage()
		{
			(_, _, var multiplexerA, var multiplexerB, _, _) = await GetSubStreamsPair();

			multiplexerB.RequestedStream += (snd, evt) => evt.GetStream(out _);

			for (var i = 0; i < ushort.MaxValue + 100; i++)
			{
				var stream = await multiplexerA.RequestStream(_ct, "x-stream");
				stream.Should().NotBeNull();
			}
		}

		[TestMethod]
		public async Task MultiplexerTestMultipleStreams()
		{
			(_, _, var multiplexerA, var multiplexerB, _, _) = await GetSubStreamsPair();

			var aStreams = ImmutableDictionary<string, Stream>.Empty;
			var bStreams = ImmutableDictionary<string, Stream>.Empty;

			multiplexerA.RequestedStream +=
				(snd, evt) =>
				{
					if (evt.GetStream(out var stream))
					{
						ImmutableInterlocked.TryAdd(ref aStreams, evt.Name, stream);
					}
				};

			const int nbrStreams = 1000;

			for (var i = 0; i < nbrStreams; i++)
			{
				var name = "stream" + i;
				while (true)
				{
					var s = await multiplexerB.RequestStream(_ct, name);
					if (s != null)
					{
						ImmutableInterlocked.TryAdd(ref bStreams, name, s);
						break;
					}
				}
			}

			aStreams.Should().HaveCount(nbrStreams);
			bStreams.Should().HaveCount(nbrStreams);

			foreach(var aStream in aStreams)
			{
				var stream = aStream.Value;
				var bytes = Encoding.UTF32.GetBytes(aStream.Key);
				await stream.WriteAsync(bytes, 0, bytes.Length, _ct);
				await stream.FlushAsync(_ct);
			}

			var readBuffer = new byte[50];
			foreach (var bStream in bStreams)
			{
				var stream = bStream.Value;
				var expectedBytes = Encoding.UTF32.GetBytes(bStream.Key);
				var read = await stream.ReadAsync(readBuffer, 0, 50, _ct);
				read.Should().Be(expectedBytes.Length);
				readBuffer.Take(read).SequenceEqual(expectedBytes).Should().BeTrue(bStream.Key);
			}
		}

		[TestMethod]
		public async Task TestStreamWindowSize()
		{
			(_, _, _, _, var subStreamA, var subStreamB) = await GetSubStreamsPair();

			subStreamB.WriteByte(1);
			await subStreamB.FlushAsync(_ct); // available window size reduced to 1

			subStreamB.WriteByte(2);
			await subStreamB.FlushAsync(_ct); // available window size reduced to 0

			subStreamB.WriteByte(2);
			var t = subStreamB.FlushAsync(_ct); // should be blocked until something is read from subStreamA

			await Task.Delay(120, _ct); // give time for connection to do something
			t.IsCompleted.Should().BeFalse();

			// reading in subStreamA should unblock it
			var buffer = new byte[10];
			await subStreamA.ReadAsync(buffer, 0, 10, _ct);

			await Task.Delay(120, _ct); // give time for connection to do something
			t.IsCompleted.Should().BeTrue();
		}

		[TestMethod]
		[Ignore]
		public async Task TestTransmitBigStream()
		{
			(_, _, _, _, var subStreamA, var subStreamB) = await GetSubStreamsPair();

			var bigStream = new MemoryStream();

			const int length = 1 * 1024 * 1024 + 3000000;

			long sum = 0;
			for(var i = 0;i < length; i++)
			{
				byte b = (byte) (i % 256);
				bigStream.WriteByte(b);
				sum += b;
			}
			bigStream.Position = 0;

			// Launch copy
			var copyTask = bigStream.CopyToAsync(subStreamA, 2048, _ct).ContinueWith(_ => subStreamA.Dispose(), _ct);

			long readSum = 0;
			var buffer = new byte[64];
			while (true)
			{
				var read = await subStreamB.ReadAsync(buffer, 0, 64, _ct);
				if (read == 0)
				{
					break; // this should not happend until the reading buffer is cleared
				}
				var s = buffer.Take(read).Sum(x=>x);
				readSum += s;
				await Task.Yield();
			}

			copyTask.IsCompleted.Should().BeTrue();

			readSum.Should().Be(sum);
		}

		[TestMethod]
		public async Task TestTransmitAndClose()
		{
			(_, _, _, _, var subStreamA, var subStreamB) = await GetSubStreamsPair();

			async Task WriteToStream()
			{
				await Task.Yield(); // run the remaining asynchrounously

				for (byte i = 0; i < 10; i++)
				{
					var wbuffer = Enumerable.Repeat(i, i + 1).ToArray();
					await subStreamB.WriteAsync(wbuffer, 0, wbuffer.Length, _ct);
					await subStreamB.FlushAsync(_ct);
				}

				subStreamB.Close();
			}

			// Launch writing
			var writeTask = WriteToStream();

			for (byte j = 0; j < 10; j++)
			{
				await Task.Delay(5, _ct);

				var buffer = new byte[j + 1];
				var read = await subStreamA.ReadAsync(buffer, 0, buffer.Length, _ct);
				read.Should().Be(buffer.Length);
				buffer.Should().AllBeEquivalentTo(j);
			}

			writeTask.IsCompleted.Should().BeTrue();

			var b = new byte[1];
			var r = await subStreamA.ReadAsync(b, 0, 1, _ct);
			r.Should().Be(0);
		}

		[TestMethod]
		public async Task TestTransmitWithoutFlushingAndClose()
		{
			(_, _, _, _, var subStreamA, var subStreamB) = await GetSubStreamsPair();

			async Task WriteToStream()
			{
				await Task.Yield(); // run the remaining asynchrounously

				for (byte i = 0; i < 10; i++)
				{
					var wbuffer = Enumerable.Repeat(i, i + 1).ToArray();
					await subStreamB.WriteAsync(wbuffer, 0, wbuffer.Length, _ct);
				}

				subStreamB.Close();
			}

			// Launch writing
			var writeTask = WriteToStream();

			for (byte j = 0; j < 10; j++)
			{
				await Task.Delay(5, _ct);

				var buffer = new byte[j + 1];
				var read = await subStreamA.ReadAsync(buffer, 0, buffer.Length, _ct);
				read.Should().Be(buffer.Length);
				buffer.Should().AllBeEquivalentTo(j);
			}

			writeTask.IsCompleted.Should().BeTrue();

			var b = new byte[1];
			var r = await subStreamA.ReadAsync(b, 0, 1, _ct);
			r.Should().Be(0);
		}

		// *** PRIVATE STUFF ***
		private async Task<(NetworkStream streamA, NetworkStream streamB)> GetStreamsPair()
		{
			var tcpServer = new TcpListener(IPAddress.IPv6Loopback, 0);
			tcpServer.Start();
			var serverEndpoint = (IPEndPoint)tcpServer.LocalEndpoint;

			var clientATask = tcpServer.AcceptTcpClientAsync();
			
			var clientB = new TcpClient(AddressFamily.InterNetworkV6);
			await clientB.ConnectAsync(serverEndpoint.Address, serverEndpoint.Port);

			var clientA = await clientATask;

			clientA.Connected.Should().BeTrue("A not connected.");
			clientB.Connected.Should().BeTrue("B not connected.");

			return (clientA.GetStream(), clientB.GetStream());
		}

		private async Task<(NetworkStream streamA, NetworkStream streamB, Multiplexer multiplexerA, Multiplexer multiplexerB, Multiplexer.MultiplexerStream subStreamA, Multiplexer.MultiplexerStream subStreamB)> GetSubStreamsPair()
		{
			(var streamA, var streamB) = await GetStreamsPair();

			var multiplexerA = new Multiplexer(streamA, windowSize: 2);
			var multiplexerB = new Multiplexer(streamB, windowSize: 8);

			Multiplexer.MultiplexerStream subStreamA = null;

			void OnRequestedStream(object snd, StreamRequestEventArgs evt)
			{
				if (evt.Name == "channel1")
				{
					if (evt.GetStream(out var s))
					{
						subStreamA = s as Multiplexer.MultiplexerStream;
					}
				}
			}

			multiplexerA.RequestedStream += OnRequestedStream;

			multiplexerA.Start();
			multiplexerB.Start();

			var subStreamB = await multiplexerB.RequestStream(_ct, "channel1") as Multiplexer.MultiplexerStream;

			multiplexerA.NumberOfActiveStreams.Should().Be(1);
			multiplexerB.NumberOfActiveStreams.Should().Be(1);

			subStreamA.Should().NotBeNull();
			subStreamB.Should().NotBeNull();

			return (streamA, streamB, multiplexerA, multiplexerB, subStreamA, subStreamB);
		}
	}
}
