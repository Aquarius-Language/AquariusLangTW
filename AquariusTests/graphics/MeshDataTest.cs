using System.Buffers.Binary;
using AquariusLang.Graphics;
namespace AquariusTests.Graphics;
public class MeshDataTest {
    private static byte[] Mesh() {
        var bytes=new byte[112];BinaryPrimitives.WriteUInt32LittleEndian(bytes,0x314d5141);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),3);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8),8);
        for(int i=0;i<3;i++)BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16+i*32+20),1);
        return bytes;
    }
    [Fact] public void Float32TriangleListPreservesAllComponents() {
        var bytes=Mesh();BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16),-2.25f);
        var values=MeshData.Decode(bytes);Assert.Equal(24,values.Length);Assert.Equal(-2.25f,values[0]);Assert.Equal(1,values[5]);
    }
    [Theory] [InlineData(0,0)][InlineData(4,0)][InlineData(4,2)][InlineData(4,1000002)][InlineData(8,9)][InlineData(12,1)]
    public void HeaderAndCountAreValidatedBeforeAllocation(int offset,uint value) {
        var bytes=Mesh();BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset),value);Assert.Throws<ArgumentException>(()=>MeshData.Decode(bytes));
    }
    [Fact] public void TruncationNonfiniteComponentsAndZeroNormalsFail() {
        Assert.Throws<ArgumentException>(()=>MeshData.Decode(Mesh().AsSpan(0,111)));
        foreach(float value in new[]{float.NaN,float.PositiveInfinity}){var bytes=Mesh();BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16),value);Assert.Throws<ArgumentException>(()=>MeshData.Decode(bytes));}
        var zero=Mesh();BinaryPrimitives.WriteSingleLittleEndian(zero.AsSpan(36),0);Assert.Throws<ArgumentException>(()=>MeshData.Decode(zero));
    }
}
