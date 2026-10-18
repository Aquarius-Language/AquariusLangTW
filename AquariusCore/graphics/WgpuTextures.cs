using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.IO;
using System.IO.Compression;

namespace AquariusLang.Graphics;

/// <summary>Portable, filterable WebGPU textures. Bytes are mip-major, then layer-major.</summary>
public sealed record WgpuTextureDescriptor(int Width, int Height, string Format, string Dimension, int MipLevels) {
    public int Layers => Dimension == "cube" ? 6 : 1;
    public int BytesPerPixel => Format == "rgba16float" ? 8 : Format == "r8unorm" ? 1 : 4;
    public int ByteLength {
        get {
            if (Width is < 1 or > 8192 || Height is < 1 or > 8192 ||
                Dimension is not ("2d" or "cube") || Dimension == "cube" && Width != Height ||
                Format is not ("r8unorm" or "rgba8unorm" or "rgba8unorm-srgb" or "rgba16float") ||
                MipLevels < 1 || MipLevels > 1 + (int)Math.Log2(Math.Max(Width, Height)))
                throw new ArgumentException("Invalid texture dimensions, format, dimension or mip count.");
            long total = 0;
            for (int mip = 0; mip < MipLevels; mip++) total += (long)Math.Max(1, Width >> mip) * Math.Max(1, Height >> mip) * Layers * BytesPerPixel;
            if (total > 128 * 1024 * 1024) throw new ArgumentException("Texture data exceeds 128 MiB.");
            return (int)total;
        }
    }
    public void Validate(ReadOnlySpan<byte> bytes) {
        if (bytes.Length != ByteLength) throw new ArgumentException("Texture data length does not match its descriptor.");
        if (Format == "rgba16float") for (int i = 0; i < bytes.Length; i += 2)
            if ((bytes[i + 1] & 0x7c) == 0x7c) throw new ArgumentException("Texture components must be finite half floats.");
    }
    public byte[] Decode(byte[] bytes) {
        if(bytes.Length>=2 && bytes[0]==31 && bytes[1]==139) {
            using var input=new GZipStream(new MemoryStream(bytes),CompressionMode.Decompress);
            var decoded=new byte[ByteLength];input.ReadExactly(decoded);
            if(input.ReadByte()!=-1)throw new ArgumentException("Texture data length does not match its descriptor.");
            bytes=decoded;
        }
        Validate(bytes);return bytes;
    }
}

/// <summary>Group 2 has up to eight named textures, a filtering sampler at binding 8 and
/// up to seven read-only storage buffers at bindings 9..15.</summary>
public sealed class WgpuTextureLayout {
    public readonly Dictionary<string, (int Binding, string Dimension)> Fields = new(StringComparer.Ordinal);
    public readonly Dictionary<string, int> Buffers = new(StringComparer.Ordinal);
    public WgpuTextureLayout(string source) {
        source = Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", "");
        var used = new HashSet<int>();
        foreach (Match m in Regex.Matches(source, @"@(?<a>group|binding)\s*\(\s*(?<x>\d+)\s*\)\s*@(?<b>group|binding)\s*\(\s*(?<y>\d+)\s*\)\s*var\s*<(?<space>[^>]*)>\s*(?<name>\w+)\s*:")) {
            if (m.Groups["a"].Value == m.Groups["b"].Value) continue;
            int group = int.Parse(m.Groups[m.Groups["a"].Value == "group" ? "x" : "y"].Value);
            if (group != 2) continue;
            int binding = int.Parse(m.Groups[m.Groups["a"].Value == "binding" ? "x" : "y"].Value);
            string space = Regex.Replace(m.Groups["space"].Value, @"\s+", "");
            if (binding is < 9 or > 15 || space is not ("storage" or "storage,read"))
                throw new ArgumentException("Group 2 storage buffers must be var<storage, read> at bindings 9..15.");
            if (!used.Add(binding)) throw new ArgumentException("Duplicate texture group binding.");
            if (!Buffers.TryAdd(m.Groups["name"].Value, binding)) throw new ArgumentException("Duplicate storage buffer name.");
        }
        foreach (Match m in Regex.Matches(source, @"@(?<a>group|binding)\s*\(\s*(?<x>\d+)\s*\)\s*@(?<b>group|binding)\s*\(\s*(?<y>\d+)\s*\)\s*var\s+(?<name>\w+)\s*:\s*(?<type>[^;]+);")) {
            if (m.Groups["a"].Value == m.Groups["b"].Value) continue;
            int group = int.Parse(m.Groups[m.Groups["a"].Value == "group" ? "x" : "y"].Value);
            if (group != 2) continue;
            int binding = int.Parse(m.Groups[m.Groups["a"].Value == "binding" ? "x" : "y"].Value);
            string type = Regex.Replace(m.Groups["type"].Value, @"\s+", "");
            if (!used.Add(binding)) throw new ArgumentException("Duplicate texture group binding.");
            if (binding == 8 && type == "sampler") continue;
            if (binding is < 0 or > 7 || type is not ("texture_2d<f32>" or "texture_cube<f32>"))
                throw new ArgumentException("Group 2 supports 2D/cube float textures at 0..7 and a sampler at 8.");
            if (!Fields.TryAdd(m.Groups["name"].Value, (binding, type == "texture_cube<f32>" ? "cube" : "2d")))
                throw new ArgumentException("Duplicate texture name.");
        }
    }
}
