using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AquariusLang.Wasm;

public sealed record WasmConstant(string Type, double Number = 0, string? Text = null, bool Boolean = false,
    int Function = -1, string[]? Parameters = null, string? Operation = null, string? Display = null);
public sealed record WasmFunction(bool WrapReturn, WasmConstant[] Pool);
public sealed record WasmMetadata(int AbiVersion, string Entry, Dictionary<string, int> Modules,
    WasmFunction[] Functions, Dictionary<string, string> Assets);

/// <summary>A standard WebAssembly module with bounded, versioned application metadata in a custom section.</summary>
public sealed class WasmProgram
{
    public const int MaxBytes = 256 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly byte[] bytes;
    public ReadOnlyMemory<byte> Bytes => bytes;
    public WasmMetadata Metadata { get; }
    internal WasmProgram(byte[] bytes, WasmMetadata metadata) { this.bytes = bytes; Metadata = metadata; }
    internal static byte[] SerializeMetadata(WasmMetadata metadata) => JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
    public static WasmProgram Load(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > MaxBytes) throw new InvalidDataException("Application exceeds the size limit.");
        byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return Load(bytes);
    }
    public static WasmProgram Load(byte[] bytes)
    {
        if (bytes.Length > MaxBytes || bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0, 97, 115, 109, 1, 0, 0, 0 }))
            throw new InvalidDataException("Expected a WebAssembly 1.0 application module.");
        int p = 8; WasmMetadata? metadata = null;
        uint U(int end)
        {
            uint n = 0; for (int shift = 0; shift < 35; shift += 7)
            {
                if (p >= end) throw new InvalidDataException("Truncated WebAssembly section.");
                byte b = bytes[p++]; if (shift == 28 && (b & 240) != 0) throw new InvalidDataException("Invalid WebAssembly length.");
                n |= (uint)(b & 127) << shift; if ((b & 128) == 0) return n;
            }
            throw new InvalidDataException("Invalid WebAssembly length.");
        }
        while (p < bytes.Length)
        {
            byte id = bytes[p++]; uint length = U(bytes.Length);
            if (length > (uint)(bytes.Length - p)) throw new InvalidDataException("Truncated WebAssembly section.");
            int end = p + (int)length;
            if (id == 0)
            {
                uint nameLength = U(end); if (nameLength > (uint)(end - p)) throw new InvalidDataException("Invalid custom section name.");
                string name = Encoding.UTF8.GetString(bytes, p, (int)nameLength); p += (int)nameLength;
                if (name == WasmAbi.MetadataSection)
                {
                    if (metadata != null) throw new InvalidDataException("Duplicate Aquarius metadata.");
                    try { metadata = JsonSerializer.Deserialize<WasmMetadata>(bytes.AsSpan(p, end - p), JsonOptions); }
                    catch (JsonException e) { throw new InvalidDataException("Invalid Aquarius metadata.", e); }
                }
            }
            p = end;
        }
        if (metadata == null || metadata.AbiVersion != WasmAbi.Version || metadata.Modules == null || metadata.Functions == null || metadata.Assets == null)
            throw new InvalidDataException("Missing or incompatible Aquarius WebAssembly ABI.");
        if (metadata.Modules.Count == 0 || metadata.Modules.Count + metadata.Assets.Count > 10000 || metadata.Functions.Length > 100000)
            throw new InvalidDataException("Invalid application module count.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in metadata.Modules)
        {
            ValidatePath(m.Key); if (!m.Key.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase) || !paths.Add(m.Key) || m.Value < 0 || m.Value >= metadata.Functions.Length)
                throw new InvalidDataException("Invalid application module.");
        }
        foreach (var a in metadata.Assets)
        {
            ValidatePath(a.Key); if (a.Value == null || !paths.Add(a.Key) || a.Key.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid application asset.");
            try { Convert.FromBase64String(a.Value); } catch (FormatException e) { throw new InvalidDataException("Invalid asset encoding.", e); }
        }
        if (metadata.Entry == null || !metadata.Modules.ContainsKey(metadata.Entry)) throw new InvalidDataException("Missing application entry.");
        foreach (var f in metadata.Functions)
        {
            if (f?.Pool == null) throw new InvalidDataException("Missing function constants.");
            foreach (var c in f.Pool)
            {
                if (c == null || !new[] { "name", "int", "float", "double", "string", "bool", "null", "break", "assignment", "function", "program" }.Contains(c.Type)) throw new InvalidDataException("Invalid constant type.");
                if (c.Type is "function" or "program" && (c.Function < 0 || c.Function >= metadata.Functions.Length)) throw new InvalidDataException("Invalid function reference.");
                if (c.Type == "function" && c.Parameters == null || c.Type is "name" or "string" or "assignment" && c.Text == null) throw new InvalidDataException("Invalid constant metadata.");
                if (c.Type == "assignment" && c.Operation is not ("Add" or "Subtract" or "Multiply" or "Divide")) throw new InvalidDataException("Invalid assignment operation.");
                if (c.Type == "function" && c.Parameters!.Any(p => string.IsNullOrWhiteSpace(p))) throw new InvalidDataException("Invalid function parameter.");
                if (c.Type == "int" && (c.Number < int.MinValue || c.Number > int.MaxValue || c.Number != Math.Truncate(c.Number))) throw new InvalidDataException("Invalid integer constant.");
            }
        }
        return new WasmProgram((byte[])bytes.Clone(), metadata);
    }
    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(x => x is "" or "." or ".." || x.EndsWith(' ') || x.EndsWith('.') || System.Text.RegularExpressions.Regex.IsMatch(x.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) || x.Any(c => c < 32 || ":*?\"<>|".Contains(c))))
            throw new InvalidDataException($"Invalid application path: {path}");
    }
}
