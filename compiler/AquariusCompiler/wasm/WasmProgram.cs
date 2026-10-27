using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AquariusLang.Wasm;

/// <summary>Application metadata (the <c>aquarius.application</c> custom section). Function layouts live in the program's
/// data segment, see <see cref="WasmProgram.Functions"/>.</summary>
/// <param name="Imports">ABI 4: every import name of a bundled Aquarius-source library module, with the module's path.</param>
/// <param name="Libraries">ABI 4: the native libraries the application needs.</param>
public sealed record WasmMetadata(int AbiVersion, string Entry, IReadOnlyDictionary<string, int> Modules, int FunctionCount,
    IReadOnlyList<string> AssetNames, int ValueAbi = 2, int ProgramAddress = 0, int HeapStart = 0, int FunctionLayout = 40,
    string[]? SupportedPlatforms = null, bool ExternalAssets = false,
    IReadOnlyDictionary<string, string>? Imports = null, IReadOnlyList<string>? Libraries = null)
{
    public IReadOnlyDictionary<string, string> ImportTable => Imports ?? new Dictionary<string, string>();
    public IReadOnlyList<string> NativeLibraries => Libraries ?? Array.Empty<string>();
}
/// <summary>One compiled function's frame layout as recorded in the program's data segment.</summary>
public sealed record WasmFunctionInfo(string[] Parameters, int StackCapacity, bool WrapReturn, bool CacheBindings,
    int NumericTable, string? NumericRecursiveName, int Locals, bool PromotedScope);

// The compiler's per-function input to the data segment.
internal sealed record WasmConstant(string Type, double Number = 0, string? Text = null, bool Boolean = false,
    int Function = -1, string[]? Parameters = null, string? Operation = null);
internal sealed record WasmFunction(bool WrapReturn, WasmConstant[] Pool, string[]? Parameters = null, int StackCapacity = 0, bool CacheBindings = false,
    int NumericTable = 0, string? NumericRecursiveName = null, int Locals = 0, bool PromotedScope = false, bool Native = false);

/// <summary>A standard WebAssembly module with bounded, versioned application metadata in custom sections.</summary>
public sealed class WasmProgram
{
    public const int MaxBytes = 256 * 1024 * 1024;
    public const string AssetSection = "aquarius.assets";
    private static readonly byte[] Magic = { 0, (byte)'A', (byte)'Q', (byte)'M' };
    private readonly byte[] bytes;
    public ReadOnlyMemory<byte> Bytes => bytes;
    public WasmMetadata Metadata { get; }
    /// <summary>Asset bytes by package path; empty when a deployment stores them beside the module (<see cref="WasmMetadata.ExternalAssets"/>).</summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Assets { get; }
    private HashSet<string>? exports;
    private WasmFunctionInfo[]? functions;
    private WasmProgram(byte[] bytes, WasmMetadata metadata, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> assets)
    { this.bytes = bytes; Metadata = metadata; Assets = assets; }

    /// <summary>Appends the metadata (and embedded assets) to a module that has no Aquarius sections.</summary>
    internal static WasmProgram Create(byte[] module, WasmMetadata metadata, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> assets)
    {
        using var output = new MemoryStream(); output.Write(module);
        void Custom(string name, Action<Stream> write)
        {
            using var content = new MemoryStream(); WriteString(content, name); write(content);
            output.WriteByte(0); WasmEncoding.WriteUnsigned(output, checked((uint)content.Length)); content.Position = 0; content.CopyTo(output);
        }
        Custom(WasmAbi.MetadataSection, s => {
            s.Write(Magic); s.WriteByte((byte)metadata.AbiVersion);
            foreach (int value in new[] { metadata.ValueAbi, metadata.ProgramAddress, metadata.HeapStart, metadata.FunctionLayout, metadata.FunctionCount })
                WasmEncoding.WriteUnsigned(s, checked((uint)value));
            var modules = metadata.Modules.ToArray();
            WasmEncoding.WriteUnsigned(s, (uint)modules.Length);
            foreach (var module in modules) { WriteString(s, module.Key); WasmEncoding.WriteUnsigned(s, checked((uint)module.Value)); }
            WasmEncoding.WriteUnsigned(s, (uint)Array.FindIndex(modules, m => m.Key == metadata.Entry));
            WasmEncoding.WriteUnsigned(s, metadata.SupportedPlatforms == null ? 0u : checked((uint)metadata.SupportedPlatforms.Length + 1));
            foreach (string platform in metadata.SupportedPlatforms ?? Array.Empty<string>()) WriteString(s, platform);
            s.WriteByte(metadata.ExternalAssets ? (byte)1 : (byte)0);
            WasmEncoding.WriteUnsigned(s, (uint)metadata.AssetNames.Count);
            foreach (string name in metadata.AssetNames) { WriteString(s, name); WasmEncoding.WriteUnsigned(s, checked((uint)assets[name].Length)); }
            if (metadata.AbiVersion < 4) return;
            // ABI 4: import names of bundled library modules -> script module index, then the needed native libraries.
            WasmEncoding.WriteUnsigned(s, (uint)metadata.ImportTable.Count);
            foreach (var import in metadata.ImportTable) {
                int index = Array.FindIndex(modules, m => m.Key.Equals(import.Value, StringComparison.OrdinalIgnoreCase));
                if (index < 0) throw new InvalidOperationException($"The import {import.Key} names a module that is not bundled: {import.Value}");
                WriteString(s, import.Key); WasmEncoding.WriteUnsigned(s, (uint)index);
            }
            WasmEncoding.WriteUnsigned(s, (uint)metadata.NativeLibraries.Count);
            foreach (string library in metadata.NativeLibraries) WriteString(s, library);
        });
        if (!metadata.ExternalAssets && metadata.AssetNames.Count > 0)
            Custom(AssetSection, s => { foreach (string name in metadata.AssetNames) s.Write(assets[name].Span); });
        var result = output.ToArray();
        return new WasmProgram(result, metadata, AssetViews(result, metadata, metadata.AssetNames.Select(n => assets[n].Length).ToArray()));
    }
    private static void WriteString(Stream stream, string value)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(value); WasmEncoding.WriteUnsigned(stream, (uint)encoded.Length); stream.Write(encoded);
    }
    // Embedded assets are views of the module's asset section; external assets are empty.
    private static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> AssetViews(byte[] bytes, WasmMetadata metadata, int[] lengths)
    {
        var assets = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        if (metadata.ExternalAssets || metadata.AssetNames.Count == 0) { foreach (string name in metadata.AssetNames) assets[name] = ReadOnlyMemory<byte>.Empty; return assets; }
        var section = Sections(bytes).Single(s => s.Name == AssetSection);
        int position = section.Payload;
        for (int i = 0; i < metadata.AssetNames.Count; i++) {
            assets[metadata.AssetNames[i]] = new ReadOnlyMemory<byte>(bytes, position, lengths[i]); position += lengths[i];
        }
        return assets;
    }

    /// <summary>Whether the module exports a function of this name.</summary>
    public bool HasExport(string name) => (exports ??= ReadExports()).Contains(name);
    private HashSet<string> ReadExports()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var section = Sections(bytes).FirstOrDefault(s => s.Id == 7);
        if (section.Id != 7) return names;
        var reader = new Reader(bytes, section.Content, section.End);
        for (uint i = 0, count = reader.Unsigned(); i < count; i++) {
            string name = reader.String(); byte kind = reader.Byte(); reader.Unsigned();
            if (kind == 0) names.Add(name);
        }
        return names;
    }

    /// <summary>Function frame layouts, decoded from the program's data segment.</summary>
    public IReadOnlyList<WasmFunctionInfo> Functions => functions ??= ReadFunctions();
    private WasmFunctionInfo[] ReadFunctions()
    {
        var (offset, length) = ProgramSegment();
        int program = Metadata.ProgramAddress, layout = Metadata.FunctionLayout;
        int Int(int address)
        {
            long relative = (long)address - program;
            if (relative < 0 || relative > length - 4) throw new InvalidDataException("Program data reference out of range.");
            return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + (int)relative));
        }
        string Text(int address)
        {
            int count = Int(address); long start = (long)address - program + 4;
            if (count < 0 || count > (length - start) / 2) throw new InvalidDataException("Program text out of range.");
            return Encoding.Unicode.GetString(bytes, offset + (int)start, count * 2);
        }
        int functionCount = Int(program), definitions = Int(program + 4);
        if (functionCount != Metadata.FunctionCount) throw new InvalidDataException("Application metadata does not match its function table.");
        var result = new WasmFunctionInfo[functionCount];
        for (int f = 0; f < functionCount; f++) {
            int definition = checked(definitions + f * layout), parameterCount = Int(definition + 4), names = Int(definition + 16), flags = Int(definition + 12);
            if (parameterCount < 0 || parameterCount > 10000) throw new InvalidDataException("Invalid function frame layout.");
            var parameters = new string[parameterCount];
            for (int p = 0; p < parameterCount; p++) parameters[p] = Text(Int(names + p * 4));
            int recursive = Int(definition + 28);
            result[f] = new WasmFunctionInfo(parameters, Int(definition + 8), (flags & 1) != 0, flags > 1,
                Int(definition + 24), recursive != 0 ? Text(recursive) : null, Int(definition + 32), (Int(definition + 36) & 1) != 0);
        }
        return result;
    }
    // The active data segment at the program address holds the function table, names and constant pools.
    private (int Offset, int Length) ProgramSegment()
    {
        foreach (var section in Sections(bytes).Where(s => s.Id == 11)) {
            var reader = new Reader(bytes, section.Content, section.End);
            for (uint i = 0, count = reader.Unsigned(); i < count; i++) {
                uint flags = reader.Unsigned(); int? address = null;
                if (flags == 2) reader.Unsigned();
                if (flags is 0 or 2) {
                    if (reader.Byte() != 0x41) throw new InvalidDataException("Unsupported data segment offset.");
                    address = reader.Signed();
                    if (reader.Byte() != 0x0b) throw new InvalidDataException("Unsupported data segment offset.");
                } else if (flags != 1) throw new InvalidDataException("Invalid data segment.");
                int length = checked((int)reader.Unsigned()), start = reader.Skip(length);
                if (address == Metadata.ProgramAddress) return (start, length);
            }
        }
        throw new InvalidDataException("Missing application program data.");
    }

    /// <summary>The same application with its asset bytes stored beside the module (web sites, APKs).</summary>
    public WasmProgram WithExternalAssets()
    {
        if (Metadata.AssetNames.Count == 0 || Metadata.ExternalAssets) return this;
        return Create(WithoutApplicationSections(), Metadata with { ExternalAssets = true }, Assets);
    }
    /// <summary>The same application starting at another of its scripts (targets that have no entry option at run time).</summary>
    public WasmProgram WithEntry(string entry)
    {
        string selected = Metadata.Modules.Keys.FirstOrDefault(k => k.Equals(entry, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Compiled module not found: {entry}");
        if (selected == Metadata.Entry) return this;
        return Create(WithoutApplicationSections(), Metadata with { Entry = selected }, Assets);
    }
    private byte[] WithoutApplicationSections()
    {
        using var output = new MemoryStream(); output.Write(bytes, 0, 8);
        foreach (var section in Sections(bytes))
            if (section.Name is not (WasmAbi.MetadataSection or AssetSection)) output.Write(bytes, section.Start, section.End - section.Start);
        return output.ToArray();
    }

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
        bytes = (byte[])bytes.Clone();
        Section? metadata = null, assets = null;
        foreach (var section in Sections(bytes)) {
            if (section.Name == WasmAbi.MetadataSection) { if (metadata != null) throw new InvalidDataException("Duplicate Aquarius metadata."); metadata = section; }
            if (section.Name == AssetSection) { if (assets != null) throw new InvalidDataException("Duplicate Aquarius assets."); assets = section; }
        }
        if (metadata is not { } m) throw new InvalidDataException("Missing or incompatible Aquarius WebAssembly ABI.");
        if (!bytes.AsSpan(m.Payload, m.End - m.Payload).StartsWith(Magic))
            throw new InvalidDataException("This application was built by an older AquariusLang compiler (ABI 2). Rebuild it from source.");
        return LoadBinary(bytes, m, assets);
    }

    private static WasmProgram LoadBinary(byte[] bytes, Section section, Section? assetSection)
    {
        var reader = new Reader(bytes, section.Payload + Magic.Length, section.End);
        int abi = reader.Byte();
        if (abi < WasmAbi.OldestVersion || abi > WasmAbi.Version) throw new InvalidDataException("Missing or incompatible Aquarius WebAssembly ABI.");
        int Int() => checked((int)reader.Unsigned());
        int valueAbi = Int(), programAddress = Int(), heapStart = Int(), functionLayout = Int(), functionCount = Int();
        if (valueAbi != 2 || programAddress != WasmRuntimeImage.ProgramAddress || heapStart < programAddress + 8 || heapStart > MaxBytes || functionLayout != 40)
            throw new InvalidDataException("Invalid Aquarius value ABI or memory layout.");
        if (functionCount <= 0 || functionCount > 100000) throw new InvalidDataException("Invalid application module count.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int moduleCount = Int();
        if (moduleCount <= 0 || moduleCount > 10000) throw new InvalidDataException("Invalid application module count.");
        var modules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); var order = new string[moduleCount];
        for (int i = 0; i < moduleCount; i++) {
            string name = reader.String(); int index = Int();
            ValidatePath(name);
            if (!name.EndsWith(".aqua", StringComparison.OrdinalIgnoreCase) || !paths.Add(name) || index >= functionCount) throw new InvalidDataException("Invalid application module.");
            modules.Add(name, index); order[i] = name;
        }
        int entry = Int(); if (entry >= moduleCount) throw new InvalidDataException("Missing application entry.");
        int platformCount = Int(); string[]? platforms = null;
        if (platformCount > 0) {
            platforms = new string[platformCount - 1];
            for (int i = 0; i < platforms.Length; i++) platforms[i] = reader.String();
            if (platforms.Length == 0 || platforms.Length > 32 || platforms.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 32))
                throw new InvalidDataException("Invalid supported platform metadata.");
        }
        byte storage = reader.Byte(); if (storage > 1) throw new InvalidDataException("Invalid asset storage.");
        int assetCount = Int(); if (moduleCount + assetCount > 10000) throw new InvalidDataException("Invalid application module count.");
        var names = new string[assetCount]; var lengths = new int[assetCount]; long total = 0;
        for (int i = 0; i < assetCount; i++) {
            names[i] = reader.String(); lengths[i] = Int(); total += lengths[i];
            ValidatePath(names[i]);
            if (!paths.Add(names[i]) || names[i].EndsWith(".aqua", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid application asset.");
        }
        Dictionary<string, string>? imports = null; List<string>? libraries = null;
        if (abi >= 4) {
            int importCount = Int();
            if (importCount > 10000) throw new InvalidDataException("Invalid library module metadata.");
            imports = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < importCount; i++) {
                string name = reader.String(); int module = Int();
                if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > 128 || module >= moduleCount || !imports.TryAdd(name, order[module]))
                    throw new InvalidDataException("Invalid library module metadata.");
            }
            int libraryCount = Int();
            if (libraryCount > 1000) throw new InvalidDataException("Invalid native library metadata.");
            libraries = new List<string>();
            for (int i = 0; i < libraryCount; i++) {
                string name = reader.String();
                if (!Regex.IsMatch(name, "^[a-z0-9_]{1,64}$") || libraries.Contains(name)) throw new InvalidDataException("Invalid native library metadata.");
                libraries.Add(name);
            }
        }
        if (!reader.AtEnd) throw new InvalidDataException("Invalid Aquarius metadata.");
        bool external = storage == 1;
        long stored = assetSection is { } a ? a.End - a.Payload : -1;
        if (external ? stored >= 0 : assetCount == 0 ? stored >= 0 : stored != total) throw new InvalidDataException("Application assets do not match their metadata.");
        var metadata = new WasmMetadata(abi, order[entry], modules, functionCount, names, valueAbi, programAddress, heapStart, functionLayout, platforms, external,
            imports, libraries);
        var program = new WasmProgram(bytes, metadata, AssetViews(bytes, metadata, lengths));
        program.ValidateLayout();
        return program;
    }
    // The data segment must end at the heap and describe sane frames.
    private void ValidateLayout()
    {
        if (Metadata.HeapStart != Metadata.ProgramAddress + ProgramSegment().Length) throw new InvalidDataException("Application metadata does not match its memory layout.");
        foreach (var f in Functions) {
            if (f.StackCapacity < 16 || f.StackCapacity > 1000000 || f.Parameters.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Invalid function frame layout.");
            if (f.NumericTable < 0 || f.NumericTable > Metadata.FunctionCount * 4 + 1 || f.NumericRecursiveName != null && (string.IsNullOrWhiteSpace(f.NumericRecursiveName) || f.NumericTable == 0))
                throw new InvalidDataException("Invalid numeric function layout.");
            if (f.Locals < 0 || f.Locals > 1000000 || f.PromotedScope && f.Locals < f.Parameters.Length) throw new InvalidDataException("Invalid frame local layout.");
        }
    }

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(x => x is "" or "." or ".." || x.EndsWith(' ') || x.EndsWith('.') || Regex.IsMatch(x.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase) || x.Any(c => c < 32 || ":*?\"<>|".Contains(c))))
            throw new InvalidDataException($"Invalid application path: {path}");
    }

    // ---- Module structure ------------------------------------------------------------------------------------------

    /// <summary>A top-level section; custom sections carry their name and the offset of their payload.</summary>
    private readonly record struct Section(byte Id, int Start, int Content, int End, string? Name, int Payload);
    private static IEnumerable<Section> Sections(byte[] bytes)
    {
        int position = 8;
        while (position < bytes.Length) {
            var reader = new Reader(bytes, position, bytes.Length);
            int start = position; byte id = reader.Byte(); uint length = reader.Unsigned();
            if (length > (uint)(bytes.Length - reader.Position)) throw new InvalidDataException("Truncated WebAssembly section.");
            int content = reader.Position, end = content + (int)length;
            string? name = null; int payload = content;
            if (id == 0) { var custom = new Reader(bytes, content, end); name = custom.String(); payload = custom.Position; }
            yield return new Section(id, start, content, end, name, payload);
            position = end;
        }
    }
    /// <summary>A bounds-checked LEB128/UTF-8 reader over part of a module.</summary>
    private sealed class Reader(byte[] bytes, int position, int end)
    {
        public int Position => position;
        public bool AtEnd => position == end;
        public byte Byte() => position < end ? bytes[position++] : throw new InvalidDataException("Truncated WebAssembly section.");
        public uint Unsigned()
        {
            uint n = 0;
            for (int shift = 0; shift < 35; shift += 7) {
                byte b = Byte(); if (shift == 28 && (b & 240) != 0) throw new InvalidDataException("Invalid WebAssembly length.");
                n |= (uint)(b & 127) << shift; if ((b & 128) == 0) return n;
            }
            throw new InvalidDataException("Invalid WebAssembly length.");
        }
        public int Signed()
        {
            int n = 0, shift = 0; byte b;
            do { b = Byte(); n |= (b & 127) << shift; shift += 7; } while ((b & 128) != 0 && shift < 35);
            if ((b & 128) != 0) throw new InvalidDataException("Invalid WebAssembly integer.");
            return shift < 32 && (b & 64) != 0 ? n | -(1 << shift) : n;
        }
        public int Skip(int length)
        {
            if (length < 0 || length > end - position) throw new InvalidDataException("Truncated WebAssembly section.");
            int start = position; position += length; return start;
        }
        public string String()
        {
            int length = checked((int)Unsigned()), start = Skip(length);
            try { return new UTF8Encoding(false, true).GetString(bytes, start, length); }
            catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid UTF-8 name.", e); }
        }
    }
}
