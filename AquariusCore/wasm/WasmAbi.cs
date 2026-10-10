using System;
using System.Collections.Generic;

namespace AquariusLang.Wasm;

/// <summary>The capability boundary. Wasm owns language values, frames, scopes and scheduling.</summary>
public static class WasmAbi
{
    public const int Version = 2;
    public const string ImportModule = "aquarius_v2";
    public const string MetadataSection = "aquarius.application";
    public const int Quantum = 4096;
}

public interface IWasmEngine
{
    IWasmInstance Instantiate(ReadOnlyMemory<byte> module, IReadOnlyDictionary<string, Delegate> imports);
}
public interface IWasmInstance : IDisposable
{
    int Invoke(string name, params int[] arguments);
    Span<byte> Memory(int address, int length);
}
