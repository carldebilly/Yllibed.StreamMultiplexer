using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Yllibed.StreamMultiplexer.Core
{
	internal static class BufferExtensions
	{
		internal static void WriteStructToBuffer<T>(this byte[] buffer, T structData, ushort offset= 0 )
			where T : struct
		{
			// Set the buffer to the correct size
			var structSize = Marshal.SizeOf<T>();

			if (buffer.Length < structSize + offset)
			{
				throw new ArgumentOutOfRangeException(nameof(offset), "Buffer too short");
			}

			// Allocate the buffer to memory and pin it so that GC cannot use the space
			var h = GCHandle.Alloc(buffer, GCHandleType.Pinned);

			// Copy struct data over the buffer at specified offset
			Marshal.StructureToPtr(structData, h.AddrOfPinnedObject() + offset, fDeleteOld: false);

			// Allow the GC again on this memory region
			h.Free();
		}

		internal static T ReadStructFromBuffer<T>(this byte[] buffer, ushort offset = 0)
			where T : struct
		{
			// Set the buffer to the correct size
			var structSize = Marshal.SizeOf<T>();

			if (buffer.Length < structSize + offset)
			{
				throw new ArgumentOutOfRangeException(nameof(offset), "Buffer too short");
			}

			// Allocate the buffer to memory and pin it so that GC cannot use the space
			var h = GCHandle.Alloc(buffer, GCHandleType.Pinned);

			// Copy struct data from the buffer at specified offset
			var data = Marshal.PtrToStructure<T>(h.AddrOfPinnedObject() + offset);

			// Allow the GC again on this memory region
			h.Free();

			return data;
		}
	}
}
