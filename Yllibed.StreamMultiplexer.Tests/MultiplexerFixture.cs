using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
				: new CancellationTokenSource(5000);
			_ct = _cts.Token;
		}

		[TestCleanup]
		public void Teardown()
		{
			_cts.Cancel();
		}

		[TestMethod]
		public async Task MultiplexerSanityTest()
		{
			(var streamA, var streamB) = await GetStreamsPair();

			var multiplexerA = new Multiplexer(streamA);
			var multiplexerB = new Multiplexer(streamB);

			Stream subStreamA = null;
			multiplexerA.RequestedStream += (snd, evt) => { evt.GetStream(out subStreamA); };

			multiplexerA.Start();
			multiplexerB.Start();

			var subStreamB = await multiplexerB.RequestStream(_ct, "channel");

			multiplexerA.NumberOfActiveStreams.ShouldBeEquivalentTo(1);
			multiplexerB.NumberOfActiveStreams.ShouldBeEquivalentTo(1);

			subStreamA.Should().NotBeNull();
			subStreamB.Should().NotBeNull();

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
			readFromA.ShouldBeEquivalentTo(10);
			bufferA.ShouldAllBeEquivalentTo(bufferB);

			subStreamB.Dispose();
			multiplexerB.NumberOfActiveStreams.ShouldBeEquivalentTo(0);

			subStreamA.ReadByte().ShouldBeEquivalentTo(-1);
			multiplexerA.NumberOfActiveStreams.ShouldBeEquivalentTo(0);

		}

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
	}
}
