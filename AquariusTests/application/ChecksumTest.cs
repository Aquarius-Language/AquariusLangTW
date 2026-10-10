using System.Text;
using AquariusLang.Application;

namespace AquariusTests.Application;

public class ChecksumTest {
    [Theory]
    [InlineData("", 0u, 1u)]
    [InlineData("123456789", 0xcbf43926u, 0x091e01deu)]
    public void ChecksumsMatchStandardVectors(string text, uint crc, uint adler) {
        var bytes = Encoding.ASCII.GetBytes(text);
        Assert.Equal(crc, DocumentSerialization.Crc32(bytes));
        Assert.Equal(adler, DocumentSerialization.Adler32(bytes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(5551)]
    [InlineData(5552)]
    [InlineData(5553)]
    [InlineData(11104)]
    [InlineData(1024 * 1024)]
    public void ChecksumsPreserveResultsAcrossBlockBoundariesAndWorstCaseInput(int length) {
        var bytes = new byte[length];
        Array.Fill(bytes, byte.MaxValue);
        Check(bytes);
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 73);
        Check(bytes);
    }

    private static void Check(byte[] bytes) {
        // Independent bitwise/per-byte reference also catches block accumulator overflow.
        uint crc = uint.MaxValue, a = 1, b = 0;
        foreach (byte value in bytes) {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
            a = (a + value) % 65521; b = (b + a) % 65521;
        }
        Assert.Equal(~crc, DocumentSerialization.Crc32(bytes));
        Assert.Equal(b << 16 | a, DocumentSerialization.Adler32(bytes));
    }
}
