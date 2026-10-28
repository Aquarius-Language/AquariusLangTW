using System.IO;
using System.Text;

namespace AquariusLang.Wasm;

/// <summary>
/// The only Wasm binary encoder of the compiler: LEB128 integers, names and the instruction immediates every
/// emitter uses. Emitters import it with <c>using static</c>; an architecture test rejects private copies.
/// The short names follow the emitters' existing idiom: U = unsigned LEB128, S = signed LEB128,
/// I/L/F = i32/i64/f64 constants, Get/Set/Tee = local access, Memory = a load/store with its memarg.
/// </summary>
internal static class WasmEncoding
{
    internal const byte I32Const = 0x41, I64Const = 0x42, F64Const = 0x44, LocalGet = 0x20, LocalSet = 0x21, LocalTee = 0x22,
        I32Load = 0x28, I64Load = 0x29, F32Load = 0x2a, F64Load = 0x2b, I32Load8U = 0x2d, I32Store = 0x36, I64Store = 0x37, F32Store = 0x38, F64Store = 0x39, I32Store8 = 0x3a,
        Block = 0x02, Loop = 0x03, If = 0x04, Else = 0x05, End = 0x0b, Br = 0x0c, BrTable = 0x0e, Return = 0x0f, Call = 0x10, ReturnCall = 0x12, Drop = 0x1a;

    internal static void WriteUnsigned(Stream s, uint n) { do { byte b = (byte)(n & 127); n >>= 7; s.WriteByte((byte)(b | (n != 0 ? 128 : 0))); } while (n != 0); }
    internal static void U(BinaryWriter w, int n) => WriteUnsigned(w.BaseStream, checked((uint)n));
    internal static void S(BinaryWriter w, long n)
    {
        bool more;
        do { byte b = (byte)(n & 127); n >>= 7; more = !(n == 0 && (b & 64) == 0 || n == -1 && (b & 64) != 0); w.Write((byte)(b | (more ? 128 : 0))); } while (more);
    }
    internal static void Name(BinaryWriter w, string text) { var b = Encoding.UTF8.GetBytes(text); U(w, b.Length); w.Write(b); }
    internal static void I(BinaryWriter w, int n) { w.Write(I32Const); S(w, n); }
    internal static void L(BinaryWriter w, long n) { w.Write(I64Const); S(w, n); }
    internal static void F(BinaryWriter w, double v) { w.Write(F64Const); w.Write(v); }
    internal static void Get(BinaryWriter w, int local) { w.Write(LocalGet); U(w, local); }
    internal static void Set(BinaryWriter w, int local) { w.Write(LocalSet); U(w, local); }
    internal static void Tee(BinaryWriter w, int local) { w.Write(LocalTee); U(w, local); }
    internal static void Memory(BinaryWriter w, byte op, int align, int offset = 0) { w.Write(op); U(w, align); U(w, offset); }
}
