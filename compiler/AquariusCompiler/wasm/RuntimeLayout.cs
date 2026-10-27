using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace AquariusLang.Wasm;

/// <summary>Guest struct offsets, value tags and execution states. The values come from runtime/guest/layout.def, the one
/// table runtime.c (checked by layout.h at its build) and the native host also use; the names here only select entries.</summary>
internal static class RuntimeLayout
{
    private static readonly Dictionary<string, int> Table = Load();
    private static Dictionary<string, int> Load()
    {
        using var stream = typeof(RuntimeLayout).Assembly.GetManifestResourceStream("AquariusLang.Wasm.layout.def")
            ?? throw new InvalidOperationException("The guest layout table (runtime/guest/layout.def) is not embedded.");
        string text = new StreamReader(stream).ReadToEnd();
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, @"^AQUA_FIELD\((\w+), \w+, (\w+), (\d+)\)", RegexOptions.Multiline)) table.Add(m.Groups[1].Value + m.Groups[2].Value, int.Parse(m.Groups[3].Value));
        foreach (Match m in Regex.Matches(text, @"^AQUA_SIZE\((\w+), (\d+)\)", RegexOptions.Multiline)) table.Add(m.Groups[1].Value + "Size", int.Parse(m.Groups[2].Value));
        foreach (Match m in Regex.Matches(text, @"^AQUA_TAG\(\w+, (\w+), (\d+)\)", RegexOptions.Multiline)) table.Add(m.Groups[1].Value + "Tag", int.Parse(m.Groups[2].Value));
        foreach (Match m in Regex.Matches(text, @"^AQUA_STATUS\((\w+), (\d+)\)", RegexOptions.Multiline)) table.Add("Status" + m.Groups[1].Value, int.Parse(m.Groups[2].Value));
        return table;
    }
    private static int Get(string name) => Table.TryGetValue(name, out int value) ? value : throw new InvalidOperationException("runtime/guest/layout.def has no entry " + name + ".");

    internal static readonly int ValueTag = Get(nameof(ValueTag)), ValueRef = Get(nameof(ValueRef)), ValueNumber = Get(nameof(ValueNumber)), ValueSize = Get(nameof(ValueSize));
    internal static readonly int TextLength = Get(nameof(TextLength)), TextData = Get(nameof(TextData));
    internal static readonly int BindingName = Get(nameof(BindingName)), BindingHash = Get(nameof(BindingHash)), BindingValue = Get(nameof(BindingValue)), BindingSize = Get(nameof(BindingSize));
    internal static readonly int ScopeOuter = Get(nameof(ScopeOuter)), ScopeBindings = Get(nameof(ScopeBindings)), ScopeCount = Get(nameof(ScopeCount)), ScopeCapacity = Get(nameof(ScopeCapacity)),
        ScopeCaptured = Get(nameof(ScopeCaptured)), ScopeIndex = Get(nameof(ScopeIndex)), ScopeIndexCapacity = Get(nameof(ScopeIndexCapacity)), ScopeSize = Get(nameof(ScopeSize));
    internal static readonly int ArrayStorageReferences = Get(nameof(ArrayStorageReferences)), ArrayStorageCapacity = Get(nameof(ArrayStorageCapacity)),
        ArrayStorageUsed = Get(nameof(ArrayStorageUsed)), ArrayStorageValues = Get(nameof(ArrayStorageValues)), ArrayStorageSize = Get(nameof(ArrayStorageSize));
    internal static readonly int ArrayCount = Get(nameof(ArrayCount)), ArrayCapacity = Get(nameof(ArrayCapacity)), ArrayValues = Get(nameof(ArrayValues)), ArrayDirtyNext = Get(nameof(ArrayDirtyNext)),
        ArrayDirty = Get(nameof(ArrayDirty)), ArrayExposed = Get(nameof(ArrayExposed)), ArrayStorage = Get(nameof(ArrayStorage)), ArrayElement = Get(nameof(ArrayElement)),
        ArraySize = Get(nameof(ArraySize));
    internal static readonly int PairKey = Get(nameof(PairKey)), PairValue = Get(nameof(PairValue)), PairSize = Get(nameof(PairSize));
    internal static readonly int HashCount = Get(nameof(HashCount)), HashCapacity = Get(nameof(HashCapacity)), HashPairs = Get(nameof(HashPairs));
    internal static readonly int FunctionTable = Get(nameof(FunctionTable)), FunctionArity = Get(nameof(FunctionArity)), FunctionCapacity = Get(nameof(FunctionCapacity)),
        FunctionWrapReturn = Get(nameof(FunctionWrapReturn)), FunctionParameters = Get(nameof(FunctionParameters)), FunctionConstants = Get(nameof(FunctionConstants)),
        FunctionNumericTable = Get(nameof(FunctionNumericTable)), FunctionRecursiveName = Get(nameof(FunctionRecursiveName)), FunctionLocals = Get(nameof(FunctionLocals)),
        FunctionFlags = Get(nameof(FunctionFlags)), FunctionSize = Get(nameof(FunctionSize));
    internal static readonly int ClosureProgram = Get(nameof(ClosureProgram)), ClosureFunction = Get(nameof(ClosureFunction)), ClosureScope = Get(nameof(ClosureScope)),
        ClosureContext = Get(nameof(ClosureContext)), ClosureCache = Get(nameof(ClosureCache)), ClosureCacheEra = Get(nameof(ClosureCacheEra)), ClosureSize = Get(nameof(ClosureSize));
    internal static readonly int FrameParent = Get(nameof(FrameParent)), FrameExecution = Get(nameof(FrameExecution)), FrameProgram = Get(nameof(FrameProgram)), FrameFunction = Get(nameof(FrameFunction)),
        FrameScope = Get(nameof(FrameScope)), FrameLoops = Get(nameof(FrameLoops)), FramePc = Get(nameof(FramePc)), FrameSp = Get(nameof(FrameSp)), FrameReturned = Get(nameof(FrameReturned)),
        FrameContext = Get(nameof(FrameContext)), FrameCapacity = Get(nameof(FrameCapacity)), FramePendingBase = Get(nameof(FramePendingBase)), FramePendingCount = Get(nameof(FramePendingCount)),
        FrameBindingCache = Get(nameof(FrameBindingCache)), FrameNativeSite = Get(nameof(FrameNativeSite)), FrameNativeClosure = Get(nameof(FrameNativeClosure)),
        FrameValues = Get(nameof(FrameValues));
    internal static readonly int PendingDirtyArrays = Get(nameof(PendingDirtyArrays)), PendingDirtyHashes = Get(nameof(PendingDirtyHashes)), PendingReleasedArrays = Get(nameof(PendingReleasedArrays)),
        PendingReleasedHashes = Get(nameof(PendingReleasedHashes)), PendingHostEra = Get(nameof(PendingHostEra)), PendingSize = Get(nameof(PendingSize));
    internal static readonly int ExecutionNext = Get(nameof(ExecutionNext)), ExecutionCurrent = Get(nameof(ExecutionCurrent)), ExecutionResult = Get(nameof(ExecutionResult)),
        ExecutionBudget = Get(nameof(ExecutionBudget)), ExecutionStatus = Get(nameof(ExecutionStatus)), ExecutionNumericDepthDisabled = Get(nameof(ExecutionNumericDepthDisabled)),
        ExecutionPoll = Get(nameof(ExecutionPoll)), ExecutionNumericClosure = Get(nameof(ExecutionNumericClosure)), ExecutionNumericInnermost = Get(nameof(ExecutionNumericInnermost));
    internal static readonly int VoidTag = Get(nameof(VoidTag)), IntTag = Get(nameof(IntTag)), FloatTag = Get(nameof(FloatTag)), DoubleTag = Get(nameof(DoubleTag)), BoolTag = Get(nameof(BoolTag)),
        StringTag = Get(nameof(StringTag)), ArrayTag = Get(nameof(ArrayTag)), HashTag = Get(nameof(HashTag)), ClosureTag = Get(nameof(ClosureTag)), ModuleTag = Get(nameof(ModuleTag)),
        NativeTag = Get(nameof(NativeTag)), NullTag = Get(nameof(NullTag)), BreakTag = Get(nameof(BreakTag)), ErrorTag = Get(nameof(ErrorTag)), BuiltinTag = Get(nameof(BuiltinTag));
    internal static readonly int StatusRunning = Get(nameof(StatusRunning)), StatusYielded = Get(nameof(StatusYielded)), StatusSuspended = Get(nameof(StatusSuspended)),
        StatusFailed = Get(nameof(StatusFailed)), StatusCompleted = Get(nameof(StatusCompleted));
}
