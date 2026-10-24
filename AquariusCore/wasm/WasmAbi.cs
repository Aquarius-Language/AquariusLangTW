using System;
using System.Collections.Generic;

namespace AquariusLang.Wasm;

/// <summary>The capability boundary. Wasm owns language values, frames, scopes and scheduling.</summary>
public static class WasmAbi
{
    /// <summary>Metadata format written by this compiler: 3 is binary metadata with a raw asset section.</summary>
    public const int Version = 3;
    /// <summary>Oldest format hosts still load: 2 is JSON metadata with base64 assets. The value ABI is unchanged.</summary>
    public const int LegacyVersion = 2;
    /// <summary>Format of the program.json bundle that web and Android hosts load beside program.wasm (independent of the Wasm ABI).</summary>
    public const int BundleVersion = 2;
    public const string ImportModule = "aquarius_v2";
    public const string MetadataSection = "aquarius.application";
    public const int Quantum = 32768;
    public const int DesktopQuantum = 65536;
}

public interface IWasmEngine
{
    IWasmInstance Instantiate(ReadOnlyMemory<byte> module, IReadOnlyDictionary<string, Delegate> imports);
}
public interface IWasmInstance : IDisposable
{
    int Invoke(string name, params int[] arguments);
    Span<byte> Memory(int address, int length);
    // Typed exports are bound once and called without a name lookup or argument array. Engines override
    // these with direct bindings; wrappers that only implement Invoke keep observing every call.
    Func<int> Export(string name) => () => Invoke(name);
    Func<int, int> Export1(string name) => a => Invoke(name, a);
    Func<int, int, int> Export2(string name) => (a, b) => Invoke(name, a, b);
    Func<int, int, int, int> Export3(string name) => (a, b, c) => Invoke(name, a, b, c);
    Func<int, int, int, int, int> Export4(string name) => (a, b, c, d) => Invoke(name, a, b, c, d);
}
