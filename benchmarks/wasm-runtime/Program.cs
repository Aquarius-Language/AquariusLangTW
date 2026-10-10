using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AquariusLang.Object;
using AquariusLang.runtime;
#if LEGACY
using AquariusLang.VM;
using AquariusLang.utils;
#else
using AquariusLang.Wasm;
using AquariusLang.Desktop.runtime;
#endif

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length != 2) throw new ArgumentException("Usage: WasmProfile comparison-directory output.json");
var directory = Path.GetFullPath(args[0]);
using var comparison = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "comparison.json")));
var results = new Dictionary<string, object>();
foreach (var path in Directory.GetFiles(directory, "*.wasm").Order())
{
    string name = Path.GetFileNameWithoutExtension(path), output = "";
    var spec = comparison.RootElement.GetProperty("cases").GetProperty(name + "/wasm_precompiled");
    double expected = spec.GetProperty("expected").GetDouble();
    var builtins = new Builtins();
    builtins.BuiltinFuncs["印出"] = new BuiltinObj(values => { output = string.Join(" ", values.Select(v => v.Inspect())); return null!; });
#if LEGACY
    builtins.BuiltinFuncs["加入"] = new BuiltinObj(values => new ArrayObj(Utils.PushToArray(((ArrayObj)values[0]).Elements, values[1])));
    builtins.BuiltinFuncs["長度"] = new BuiltinObj(values => new IntegerObj(((ArrayObj)values[0]).Elements.Length));
    var program = new VmCompiler().Compile(File.ReadAllText(Path.ChangeExtension(path, ".aqua")));
    var runtime = new VirtualMachine(builtins);
#else
    builtins.BuiltinFuncs["加入"] = BuiltinObj.FromPortable(4, _ => throw new InvalidOperationException("Host push used."));
    builtins.BuiltinFuncs["長度"] = BuiltinObj.FromPortable(1, _ => throw new InvalidOperationException("Host length used."));
    var program = WasmProgram.Load(path); using var runtime = new WasmRuntime(builtins, new WasmtimeEngine());
#endif
    void Validate(IObject? value)
    {
        if (value is ErrorObj error) throw new InvalidOperationException(error.Message);
        if (!output.StartsWith("RESULT ")) throw new InvalidOperationException("Missing result.");
        var actual = double.Parse(output[7..]); var tolerances = spec.GetProperty("spec");
        double tolerance = tolerances.TryGetProperty("relative_tolerance", out var relative) ? Math.Max(tolerances.GetProperty("absolute_tolerance").GetDouble(), Math.Abs(expected) * relative.GetDouble()) : 0;
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > tolerance) throw new InvalidOperationException($"Invalid {name}: {actual}, expected {expected}");
    }
    for (int i = 0; i < 5; i++) { output = ""; Validate(runtime.Execute(program)); }
    var samples = new List<object>();
    for (int i = 0; i < 5; i++)
    {
        output = ""; long allocated = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
        var value = runtime.Execute(program); timer.Stop(); long bytes = GC.GetTotalAllocatedBytes(true) - allocated; Validate(value);
        samples.Add(new { elapsed_ms = timer.Elapsed.TotalMilliseconds, allocated_bytes = bytes });
    }
#if LEGACY
    results[name] = new { valid = true, samples, source_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(path, ".aqua")))).ToLowerInvariant(), result = output };
    Console.WriteLine($"{name}: legacy profile validated");
#else
    var counted = new Counter(new WasmtimeEngine()); using var instrumented = new WasmRuntime(builtins, counted); output = ""; Validate(instrumented.Execute(program));
    results[name] = new { valid = true, samples, native_calls = counted.NativeCalls, lookups = counted.Lookups, scheduler_entries = counted.SchedulerEntries, guest_heap_high_water_bytes = counted.Instance!.Invoke("aqua_heap_bytes"), wasm_sha256 = Convert.ToHexString(SHA256.HashData(program.Bytes.Span)).ToLowerInvariant(), result = output };
    Console.WriteLine($"{name}: {counted.NativeCalls} native calls, {counted.Lookups} lookups");
#endif
}
#if LEGACY
var binaries = new[] { typeof(VirtualMachine).Assembly.Location };
#else
var binaries = new[] { typeof(WasmRuntime).Assembly.Location, typeof(WasmtimeEngine).Assembly.Location, Path.Combine(AppContext.BaseDirectory, "Wasmtime.Dotnet.dll") };
#endif
File.WriteAllText(args[1], JsonSerializer.Serialize(new
{
#if LEGACY
    method = "Legacy bytecode program and VM reused; source compilation excluded. Five warmups and five samples. Matching minimal builtins. Managed allocated bytes are not peak memory.",
#else
    method = "Compiled Wasm module and guest session reused; source and engine compilation excluded. Five warmups and five samples. Counters run in a separate fresh session. Managed allocated bytes are not peak memory; guest heap is a high-water mark. Wasmtime persistent compilation cache enabled.",
#endif
    dotnet = System.Environment.Version.ToString(),
    process_affinity_mask = OperatingSystem.IsWindows() ? "0x" + Process.GetCurrentProcess().ProcessorAffinity.ToInt64().ToString("x") : null,
    fingerprints = binaries.ToDictionary(p => Path.GetFileName(p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant()), results
}, new JsonSerializerOptions { WriteIndented = true }));

#if !LEGACY
sealed class Counter(IWasmEngine inner) : IWasmEngine
{
    public int NativeCalls, Lookups, SchedulerEntries;
    public IWasmInstance? Instance;
    public IWasmInstance Instantiate(ReadOnlyMemory<byte> bytes, IReadOnlyDictionary<string, Delegate> imports)
    {
        var service = (Func<int, int, int, int, int, int, int, int>)imports["service"];
        var wrapped = new Dictionary<string, Delegate> { ["service"] = (Func<int, int, int, int, int, int, int, int>)((op, context, subject, name, arguments, count, output) => { if (op == 1) NativeCalls++; if (op == 0) Lookups++; return service(op, context, subject, name, arguments, count, output); }) };
        return Instance = new Counted(inner.Instantiate(bytes, wrapped), this);
    }
    sealed class Counted(IWasmInstance instance, Counter owner) : IWasmInstance
    {
        public int Invoke(string name, params int[] arguments) { if (name == "aqua_run") owner.SchedulerEntries++; return instance.Invoke(name, arguments); }
        public Span<byte> Memory(int address, int length) => instance.Memory(address, length);
        public void Dispose() => instance.Dispose();
    }
}
#endif
