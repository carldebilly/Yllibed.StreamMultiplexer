using System;
using System.Runtime.InteropServices;

namespace Yllibed.PipelineUtilities
{
	public static class MemoryExtensions
	{
		public static ArraySegment<byte> AsArray(this Memory<byte> buffer)
		{
			return AsArray((ReadOnlyMemory<byte>) buffer);
		}

		public static ArraySegment<byte> AsArray(this ReadOnlyMemory<byte> buffer)
		{
			if (!MemoryMarshal.TryGetArray(buffer, out var segment))
			{
				throw new InvalidOperationException("MemoryMarsal failed to convert Memory<byte> to byte[].");
			}

			return segment;
		}
	}
}
