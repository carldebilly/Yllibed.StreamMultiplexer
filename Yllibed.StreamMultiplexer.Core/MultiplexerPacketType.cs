namespace Yllibed.StreamMultiplexer.Core
{
	// ReSharper disable InconsistentNaming
	internal enum MultiplexerPacketType : byte
	{
		/// <summary>
		/// No OPeration - keep-alive mecanism
		/// </summary>
		/// <remarks>
		/// Stream 0 only
		/// No payload
		/// </remarks>
		NOP = 0x00,

		/// <summary>
		/// Stream request
		/// </summary>
		/// <remarks>
		/// Payload:
		/// ushort: buffersize
		/// remaining: UTF-8 (without BOM) of the requested name
		/// </remarks>
		REQ = 0x01,

		/// <summary>
		/// Stream request accepted
		/// </summary>
		/// <remarks>
		/// Payload:
		/// ushort: buffersize
		/// </remarks>
		ACK = 0x02,

		/// <summary>
		/// Stream request rejected
		/// </summary>
		/// <remarks>
		/// No payload
		/// </remarks>
		NAK = 0x03,

		/// <summary>
		/// Stream request collision
		/// </summary>
		/// <remarks>
		/// No payload
		/// </remarks>
		COL = 0x04,

		/// <summary>
		/// Raw stream data
		/// </summary>
		/// <remarks>
		/// Payload:
		/// Raw data (max 1430 bytes)
		/// followed by a CRC-32 or 0x0000
		/// </remarks>
		DATA = 0x10,

		/// <summary>
		/// Raw stream data acknowledge
		/// </summary>
		/// <remarks>
		/// No payload
		/// </remarks>
		DACK = 0x11,

		/// <summary>
		/// Stream error
		/// </summary>
		/// <remarks>
		/// Payload:
		/// ushort: error code
		/// </remarks>
		ERR = 0xFE,

		/// <summary>
		/// Stream closed
		/// </summary>
		/// <remarks>
		/// No payload
		/// </remarks>
		FIN = 0xFF
	}
}