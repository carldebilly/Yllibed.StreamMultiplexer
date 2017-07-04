namespace Yllibed.StreamMultiplexer.Core
{
	// ReSharper disable InconsistentNaming
	internal enum MultiplexerTerminationType : byte
	{
		NORMAL = 0x00,
		PROTOCOL = 0x01,
		ERROR = 0x02
	}
}