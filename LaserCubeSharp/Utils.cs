using LaserCubeSharp.Structs;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace LaserCubeSharp
{
    public static class Utils
    {
        public static T ParseResponse<T>(byte[] data) {
            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            T response = Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());

            return response;
        }

        public static T[] CopyToBuffer<T>(IEnumerable<T> source)
        {
            int initialCapacity = 1024;
            var pool = ArrayPool<T>.Shared;
            T[] buffer = pool.Rent(initialCapacity);
            int index = 0;

            try
            {
                foreach (var item in source)
                {
                    if (index >= buffer.Length)
                    {
                        T[] newBuffer = pool.Rent(buffer.Length * 2);
                        Array.Copy(buffer, newBuffer, buffer.Length);
                        pool.Return(buffer);
                        buffer = newBuffer;
                    }

                    buffer[index++] = item;
                }

                return buffer.AsSpan(0, index).ToArray();
            } finally
            {
                pool.Return(buffer);
            }
        }
        public static byte[] LaserPointToByte(LaserPoint point)
        {
            Span<byte> buffer = stackalloc byte[10];
            if (point!=null) {
                BinaryPrimitives.WriteUInt16LittleEndian(buffer[0..2], point.X);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..4], point.Y);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..6], point.R);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..8], point.G);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer[8..10], point.B);
            }
            return buffer.ToArray();
        }
    }
}

