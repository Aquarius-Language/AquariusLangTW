using System;
using System.Buffers.Binary;

namespace AquariusLang.Graphics;

/// <summary>AQM1 triangle list: little-endian float32 position, normal and UV.</summary>
public static class MeshData {
    public static float[] Decode(ReadOnlySpan<byte> bytes) {
        if (bytes.Length < 16) throw new ArgumentException("Invalid mesh header.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x314d5141 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != 8 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 0 ||
            count == 0 || count > 1000000 || count % 3 != 0 || bytes.Length != 16L + count * 32L)
            throw new ArgumentException("Invalid mesh header or length.");
        var result = new float[checked((int)count * 8)];
        for (int i = 0; i < result.Length; i++) {
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(16 + i * 4)..]);
            if (!float.IsFinite(result[i])) throw new ArgumentException("Mesh components must be finite.");
        }
        for (int i = 0; i < result.Length; i += 8) {
            double x = result[i + 3], y = result[i + 4], z = result[i + 5];
            if (x*x + y*y + z*z < 1e-20) throw new ArgumentException("Mesh normal must be nonzero.");
        }
        return result;
    }
}
