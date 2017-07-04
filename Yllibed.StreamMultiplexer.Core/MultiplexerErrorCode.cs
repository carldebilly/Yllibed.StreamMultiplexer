namespace Yllibed.StreamMultiplexer.Core
{
	// ReSharper disable InconsistentNaming
	internal enum MultiplexerErrorCode : ushort
	{
		/// <summary>
		/// An unknown packet type has been sent. (a  FIN  should follow)
		/// </summary>
		ERR_UNKNOWN_PACKET_TYPE = 0x0f01,

		/// <summary>
		/// A DATA  packet failed CRC-32 check.
		/// </summary>
		ERR_CRC_FAIL = 0x0f02,

		/// <summary>
		/// 
		/// </summary>
		ERR_BUFFER_OVERFLOW = 0x0f03,

		/// <summary>
		/// 
		/// </summary>
		ERR_TIME_OUT = 0x0f04,

		/// <summary>
		/// A packet is received exceeding the limit packet size 
		/// </summary>
		ERR_PACKET_TOO_LONG = 0x0f05,

		/// <summary>
		/// A packet length is too short to hold required information
		/// </summary>
		ERR_PACKET_TOO_SHORT = 0x0f06
	}
}