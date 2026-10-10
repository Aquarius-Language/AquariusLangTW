using System;
using System.Collections.Generic;

namespace AquariusLang.Wasm;

/// <summary>The portable host boundary. Engines execute Wasm; hosts own dynamic values and capabilities.</summary>
public static class WasmAbi
{
    public const int Version = 1;
    public const string ImportModule = "aquarius_v1";
    public const string MetadataSection = "aquarius.application";
    public const int Quantum = 4096;
    internal static readonly (string Name, int Arguments, bool Result)[] Imports = {
        ("checkpoint",0,true), ("constant",1,false), ("void",0,false), ("null",0,false),
        ("pop",0,false), ("duplicate",0,false), ("load",1,false), ("declare",1,false), ("assign",1,false),
        ("add",0,false), ("subtract",0,false), ("multiply",0,false), ("divide",0,false),
        ("less",0,false), ("greater",0,false), ("lessEqual",0,false), ("greaterEqual",0,false),
        ("equal",0,false), ("notEqual",0,false), ("and",0,false), ("or",0,false),
        ("negate",0,false), ("not",0,false), ("incrementPrefix",1,false), ("incrementPostfix",1,false),
        ("compoundAssign",1,false), ("truth",0,true), ("isBreak",0,true), ("closure",1,false),
        ("call",1,false), ("returned",0,false), ("array",1,false), ("hash",1,false), ("checkHashKey",0,false),
        ("index",0,false), ("checkArrayWrite",0,false), ("writeIndex",0,false), ("member",1,false),
        ("resolveMemberFunction",1,false), ("enterLoop",1,false), ("loopCondition",0,true),
        ("nextIteration",0,false), ("leaveLoop",0,false), ("break",0,false), ("error",1,false)
    };
}

public interface IWasmEngine
{
    IWasmInstance Instantiate(ReadOnlyMemory<byte> module, IReadOnlyDictionary<string, Delegate> imports);
}
public interface IWasmInstance : IDisposable
{
    Func<int, int> GetFunction(int index);
}
