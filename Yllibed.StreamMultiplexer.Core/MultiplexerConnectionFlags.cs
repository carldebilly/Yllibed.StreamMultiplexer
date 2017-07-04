using System;

namespace Yllibed.StreamMultiplexer.Core
{
	// ReSharper disable InconsistentNaming
	[Flags]
	internal enum MultiplexerConnectionFlags : byte
	{
		/// <summary>
		/// No special flags are used for the connection
		/// </summary>
		NO_FLAGS = 0x00,

		/// <summary>
		/// The peer supports CRC32 checks
		/// </summary>
		FLAG_CRC32 = 0x01
	}
}