﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DamienG.Security.Cryptography;

namespace Yllibed.StreamMultiplexer.Core
{
	public sealed partial class Multiplexer : IMultiplexer, IDisposable
	{
		private static readonly Encoding _Utf8 = new UTF8Encoding(false, false);

		public static byte[] DefaultAckBytes =
		{
			(byte) 'Y',
			(byte) 'l',
			(byte) 'l',
			(byte) 'i',
			(byte) 'b',
			(byte) 'e',
			(byte) 'd',
			(byte) '.',
			(byte) 'M',
			(byte) 'U',
			(byte) 'X',
			(byte) '.'
		};

		private readonly Stream _lowLevelStream;
		private readonly ushort _windowSize;
		private readonly ushort _bufferSize;
		private readonly bool _useCrc32;
		private readonly byte[] _ackBytes;

		private readonly TaskFactory _taskFactory;
		private readonly CancellationTokenSource _cts;
		private readonly CancellationToken _ct;

		private ImmutableDictionary<ushort, MultiplexerStream> _streams =
			ImmutableDictionary<ushort, MultiplexerStream>.Empty;

		public Multiplexer(Stream lowLevelStream, byte[] ackBytes = null, TaskScheduler scheduler = null,
			ushort bufferSize = 4096 * 2, ushort windowSize = 4, bool useCrc32 = true)
		{
			_lowLevelStream = lowLevelStream;
			_ackBytes = ackBytes ?? DefaultAckBytes;
			_bufferSize = bufferSize >= 4096
				? bufferSize
				: throw new ArgumentOutOfRangeException(nameof(bufferSize), "Min bufferSize is 4096.");
			_windowSize = windowSize >= 1
				? windowSize
				: throw new ArgumentOutOfRangeException(nameof(windowSize), "Window Size must be >= 1.");
			_useCrc32 = useCrc32;

			_taskFactory = new TaskFactory(scheduler ?? TaskScheduler.Default);
			_cts = new CancellationTokenSource();
			_ct = _cts.Token;
		}

		public event EventHandler<StreamRequestEventArgs> RequestedStream;

		public void Start()
		{
			_taskFactory.StartNew(ReadStartingBlock, TaskCreationOptions.LongRunning);
		}

		public async Task<Stream> RequestStream(CancellationToken ct, string name)
		{
			MultiplexerStream stream = null;
			try
			{
				while (!ct.IsCancellationRequested)
				{
					var requests = _requests;
					var streamId = GetNextStreamId();
					var tcs = new TaskCompletionSource<(MultiplexerPacketType result, ushort data)>();
					var updatedRequests = requests.SetItem(streamId, tcs);
					if (Interlocked.CompareExchange(ref _requests, updatedRequests, requests) != requests)
					{
						continue;
					}

					await SendREQ(streamId, _windowSize, name);
					(var result, var resultData) = await tcs.Task;

					ImmutableInterlocked.TryRemove(ref _requests, streamId, out _);

					if (result == MultiplexerPacketType.ACK)
					{
						stream = new MultiplexerStream(this, streamId, resultData);

						// Register the new stream into
						if (!ImmutableInterlocked.TryAdd(ref _streams, streamId, stream))
						{
							stream.Dispose(); // Another stream already registered on this stream id (weird)
							return null;
						}
					}
					else if (result == MultiplexerPacketType.NAK)
					{
						stream = null;
					}
					break;
				}
			}
			finally
			{
				if (ct.IsCancellationRequested && stream != null)
				{
					stream.Dispose();
					stream = null;
				}
			}
			return stream;
		}

		private ushort GetNextStreamId()
		{
			if (_requests.IsEmpty && _streams.IsEmpty)
			{
				return 1;
			}
			return (ushort) (_requests.Keys.Concat(_streams.Keys).Max() + 1);
		}

		public ushort NumberOfActiveStreams => (ushort)_streams.Count;

		private ImmutableDictionary<ushort, TaskCompletionSource<(MultiplexerPacketType result, ushort data)>> _requests
			= ImmutableDictionary<ushort, TaskCompletionSource<(MultiplexerPacketType result, ushort data)>>.Empty;

		private bool _initialized = false;

		private async void ReadStartingBlock()
		{
			var ackBytesLength = _ackBytes.Length;

			// Send starting block to other side
			var t1 = _lowLevelStream.WriteAsync(_ackBytes, 0, ackBytesLength, _ct);
			await _lowLevelStream.FlushAsync(_ct);

			// Read incoming starting block
			var buffer = new byte[ackBytesLength];
			var read = await _lowLevelStream.ReadAsync(buffer, 0, ackBytesLength, _ct);

			if (read != ackBytesLength || !buffer.SequenceEqual(_ackBytes))
			{
				Dispose();
				return;
			}

			await t1;
			_lowLevelStream.WriteByte(0x01);
			await _lowLevelStream.FlushAsync(_ct);

			var version = _lowLevelStream.ReadByte();
			if (version != 1)
			{
				Dispose();
				return;
			}

			_initialized = true;
#pragma warning disable 4014
			ProcessLowLevelInbound(); // start async process
#pragma warning restore 4014
		}

		public async Task ProcessLowLevelInbound()
		{
			var buffer = new byte[_bufferSize];
			ushort bufferPointer = 0;

			try
			{
				while (!_ct.IsCancellationRequested)
				{
					var readBytes = await _lowLevelStream.ReadAsync(buffer, bufferPointer, _bufferSize - bufferPointer, _ct);
					if (readBytes == 0)
					{
						// End of stream
						return;
					}

					bufferPointer += (ushort) readBytes;

					// Process any received packet
					while (bufferPointer >= 6)
					{
						var payloadLength = BitConverter.ToUInt16(buffer, 4);
						var packetLength = (ushort) (payloadLength + 6);
						if (packetLength > 1440)
						{
							await SendERR(0, MultiplexerErrorCode.ERR_PACKET_TOO_LONG);
						}

						if (bufferPointer < packetLength)
						{
							break; // this packet is incompleted, waiting for the remaining... (this should not happen often, except on low MTU networks)
						}

						var packetBytes = new byte[packetLength];

						// Copy buffer into new bytes for this packet
						Array.Copy(buffer, 0, packetBytes, 0, packetLength);

						// Move remaining of packet at beginning of the pointer & readjust pointer
						Array.Copy(buffer, packetLength, buffer, 0, bufferPointer - packetLength);
						bufferPointer -= packetLength;

						await ProcessIncomingPacket(packetBytes);
					}
				}
			}
			finally
			{
				Dispose();
			}
		}

		private async Task ProcessIncomingPacket(byte[] packetBytes)
		{
			var streamId = BitConverter.ToUInt16(packetBytes, 0);
			var packetType = (MultiplexerPacketType) packetBytes[2];
			var payloadLength = BitConverter.ToUInt16(packetBytes, 4);

			switch (packetType)
			{
				case MultiplexerPacketType.NOP:
					return; // nothing to do

				case MultiplexerPacketType.REQ:
					if (_streams.ContainsKey(streamId))
					{
						await SendCOL(streamId);
					}
					else
					{
						if (payloadLength < 2)
						{
							await SendERR(0, MultiplexerErrorCode.ERR_PACKET_TOO_SHORT);
						}
						else
						{
							var remoteWindowSize = BitConverter.ToUInt16(packetBytes, 6);
							var name = _Utf8.GetString(packetBytes, 8, payloadLength - 2);

							var args = new StreamRequestEventArgs(name, () => CreateStream(streamId, remoteWindowSize));
							RequestedStream?.Invoke(this, args);
							await (args.StreamCreated ? SendACK(streamId, _windowSize) : SendNAK(streamId));
						}
					}
					break;

				case MultiplexerPacketType.ACK:
					if (_requests.TryGetValue(streamId, out var requestForAck))
					{
						var remoteWindowSize = BitConverter.ToUInt16(packetBytes, 6);
						requestForAck.TrySetResult((packetType, remoteWindowSize));
					}
					break;

				case MultiplexerPacketType.NAK:
					if (_requests.TryGetValue(streamId, out var requestForNak))
					{
						requestForNak.TrySetResult((packetType, 0));
					}
					break;

				case MultiplexerPacketType.COL:
					if (_requests.TryGetValue(streamId, out var requestForCol))
					{
						var suggestedId = BitConverter.ToUInt16(packetBytes, 6);
						requestForCol.TrySetResult((packetType, suggestedId));
					}
					break;

				case MultiplexerPacketType.DATA:
					if (_streams.TryGetValue(streamId, out var streamData))
					{
						var dataLength = payloadLength - 4;
						var data = new byte [dataLength];
						Array.Copy(packetBytes, 6, data, 0, dataLength);
						streamData.OnReceivedBuffer(data);

						// TODO: check CRC
					}
					break;

				case MultiplexerPacketType.DACK:
					if (_streams.TryGetValue(streamId, out var streamDack))
					{
						streamDack.ReceivedDACK();
					}
					break;

				case MultiplexerPacketType.ERR:
					break; // should log

				case MultiplexerPacketType.FIN:
					if (_streams.TryGetValue(streamId, out var streamFin))
					{
						streamFin.OnReceivedClose();
					}
					break;
			}
		}

		private Stream CreateStream(ushort streamId, ushort remoteWindowSize)
		{
			if (_streams.ContainsKey(streamId))
			{
				return null; // already created;
			}
			var result = new MultiplexerStream(this, streamId, remoteWindowSize);

			while (true)
			{
				var capture = _streams;
				if (capture.ContainsKey(streamId))
				{
					result.Dispose();
					return null; // already created in a concurrent thread
				}
				var updated = capture.SetItem(streamId, result);
				if (Interlocked.CompareExchange(ref _streams, updated, capture) == capture)
				{
					break;
				}
			}

			// Successfully enlisted into streams list
			return result;
		}

		private byte[] _packetBuffer = new byte[1412];

		private async Task SendPacket(ushort streamId, MultiplexerPacketType packetType, params byte[][] payloads)
		{
			while (!_initialized)
			{
				await Task.Delay(10, _ct);
			}

			var payloadLength = 0;
			foreach (var payload in payloads)
			{
				Array.Copy(payload, 0, _packetBuffer, payloadLength + 6, payload.Length);
				payloadLength += payload.Length;
			}

			var header =
				new PacketHeader
				{
					StreamId = streamId,
					Type = packetType,
					Length = (ushort) payloadLength
				};

			_packetBuffer.WriteStructToBuffer(header);

			await _lowLevelStream.WriteAsync(_packetBuffer, 0, payloadLength + 6, _ct);
			await _lowLevelStream.FlushAsync(_ct);
		}

		private Task SendNOP()
		{
			return SendPacket(0, MultiplexerPacketType.NOP);
		}

		private Task SendREQ(ushort streamId, ushort windowSize, string name)
		{
			return SendPacket(
				streamId,
				MultiplexerPacketType.REQ,
				BitConverter.GetBytes(windowSize),
				_Utf8.GetBytes(name.Take(1024).ToArray()).Take(1404).ToArray());
		}

		private Task SendACK(ushort streamId, ushort windowSize)
		{
			return SendPacket(streamId, MultiplexerPacketType.ACK, BitConverter.GetBytes(windowSize));
		}

		private Task SendNAK(ushort streamId)
		{
			return SendPacket(streamId, MultiplexerPacketType.NAK);
		}

		private Task SendCOL(ushort streamId)
		{
			var suggestedId = GetNextStreamId();

			return SendPacket(streamId, MultiplexerPacketType.COL, BitConverter.GetBytes(suggestedId));
		}

		private Task SendDATA(ushort streamId, byte[] data, ushort length)
		{
			if (data.Length != length)
			{
				data = data.Take(length).ToArray();
			}
			uint crc32 = _useCrc32 ? Crc32.Compute(data) : (uint) 0;
			return SendPacket(streamId, MultiplexerPacketType.DATA, data, BitConverter.GetBytes(crc32));
		}

		private Task SendDACK(ushort streamId)
		{
			return SendPacket(streamId, MultiplexerPacketType.DACK);
		}

		private Task SendERR(ushort streamId, MultiplexerErrorCode errorCode)
		{
			return SendPacket(streamId, MultiplexerPacketType.ERR, BitConverter.GetBytes((ushort) errorCode));
		}

		private Task SendFIN(ushort streamId, MultiplexerTerminationType terminationType)
		{
			return SendPacket(streamId, MultiplexerPacketType.FIN, new[] {(byte) terminationType});
		}

		public void Dispose()
		{
			_cts.Cancel(false);

			foreach (var request in _requests.Values)
			{
				request.TrySetCanceled();
			}

			foreach (var stream in _streams.Values)
			{
				stream.Dispose();
			}

			_lowLevelStream.Dispose();
		}

		[StructLayout(LayoutKind.Sequential, Pack = 1, Size=6)]
		private struct PacketHeader
		{
			internal ushort StreamId;
			internal MultiplexerPacketType Type;
			internal byte Reserved;
			internal ushort Length;
		}
	}
}