using System.Diagnostics;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Wasm;
using AquariusLang.Desktop.runtime;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length != 4) throw new ArgumentException("Usage: ExecutionProfile compile|measure|pair|memory benchmark-root artifact-directory[;comparison-directory] output.json");
if (args[0] == "memory") {
    var bytes = Wasmtime.Module.ConvertText("(module (memory (export \"memory\") 1) (func (export \"value\") (result i32) i32.const 42))");
    using var instance = new WasmtimeEngine().Instantiate(bytes, new Dictionary<string, Delegate>());
    instance.Invoke("value");
    BinaryPrimitives.WriteInt32LittleEndian(instance.Memory(0, 4), 42);
    const int count = 1_000_000;
    var samples = new List<double>();
    for (int run = 0; run < 14; run++) {
        long sum = 0; var timer = Stopwatch.StartNew();
        for (int i = 0; i < count; i++) sum += BinaryPrimitives.ReadInt32LittleEndian(instance.Memory((i & 1023) * 16, 4));
        timer.Stop();
        if (sum != 42L * ((count + 1023) / 1024)) throw new InvalidOperationException("Invalid memory read.");
        if (run >= 3) samples.Add(timer.Elapsed.TotalMilliseconds);
    }
    File.WriteAllText(args[3], JsonSerializer.Serialize(new { operation = "Read one i32 from each 16-byte guest value cell", count, median_ms = samples.Order().ElementAt(samples.Count / 2), samples_ms = samples }));
    return;
}
if (args[0] == "pair") {
    string[] directories = args[2].Split(';');
    if (directories.Length != 2) throw new ArgumentException("Pair mode requires two artifact directories separated by a semicolon.");
    using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[1], "results", "latest.json")));
    var pairs = new Dictionary<string, object>();
    foreach (var item in expected.RootElement.GetProperty("benchmarks").EnumerateObject()) {
        var lines = new List<string>(); var builtins = new Builtins();
        builtins.BuiltinFuncs["印出"] = new BuiltinObj(values => { lines.Add(string.Join(" ", values.Select(v => v.Inspect()))); return null!; });
        var programs = directories.Select(d => WasmProgram.Load(Path.Combine(d, item.Name + ".wasm"))).ToArray();
        using var first = new WasmRuntime(builtins, new WasmtimeEngine());
        using var second = new WasmRuntime(builtins, new WasmtimeEngine());
        var runtimes = new[] { first, second }; var samples = new[] { new List<double>(), new List<double>() };
        for (int run = 0; run < 20; run++) for (int step = 0; step < 2; step++) {
            int variant = (run + step) % 2; lines.Clear();
            if (runtimes[variant].Execute(programs[variant]) is ErrorObj error) throw new InvalidOperationException(error.Message);
            double actual = double.Parse(lines[0][7..]), wanted = item.Value.GetProperty("expected").GetDouble();
            double tolerance = item.Value.TryGetProperty("relative_tolerance", out var relative) ? Math.Max(item.Value.GetProperty("absolute_tolerance").GetDouble(), Math.Abs(wanted) * relative.GetDouble()) : 0;
            if (!double.IsFinite(actual) || Math.Abs(actual - wanted) > tolerance) throw new InvalidOperationException("Incorrect benchmark checksum.");
            if (run >= 5) samples[variant].Add(double.Parse(lines[1][11..]));
        }
        double before = samples[0].Order().ElementAt(7), after = samples[1].Order().ElementAt(7);
        pairs[item.Name] = new { before_ms = before, after_ms = after, speedup = before / after, samples_before_ms = samples[0], samples_after_ms = samples[1],
            before_wasm_sha256 = Hash(Path.Combine(directories[0], item.Name + ".wasm")), after_wasm_sha256 = Hash(Path.Combine(directories[1], item.Name + ".wasm")),
            source_sha256 = Hash(Path.Combine(args[1], "benchmarks", "aquarius", item.Name + ".aqua")) };
        Console.WriteLine($"{item.Name}: {before:F4} -> {after:F4} ms ({before / after:F2}x), all checksums validated");
    }
    File.WriteAllText(args[3], JsonSerializer.Serialize(new { method = "Preserved artifacts, identical host; alternating order; five warmups and fifteen samples per variant; unmodified standard sources and reference checksums.",
        directories, runtime = System.Environment.Version.ToString(), host_sha256 = Hash(typeof(WasmtimeEngine).Assembly.Location), results = pairs }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
string root = Path.GetFullPath(args[1]), artifacts = Path.GetFullPath(args[2]);
using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "results", "latest.json")));
Directory.CreateDirectory(artifacts);
var rows = new Dictionary<string, object>();
foreach (var item in reference.RootElement.GetProperty("benchmarks").EnumerateObject()) {
    string source = Path.Combine(root, "benchmarks", "aquarius", item.Name + ".aqua");
    string artifact = Path.Combine(artifacts, item.Name + ".wasm");
    if (args[0] == "compile") {
        var compiled = new WasmCompiler().Compile(File.ReadAllText(source));
        File.WriteAllBytes(artifact, compiled.Bytes.ToArray());
    }
    var program = WasmProgram.Load(artifact);
    var lines = new List<string>();
    var builtins = new Builtins();
    builtins.BuiltinFuncs["印出"] = new BuiltinObj(values => { lines.Add(string.Join(" ", values.Select(v => v.Inspect()))); return null!; });
    using var runtime = new WasmRuntime(builtins, new WasmtimeEngine());
    double Run() {
        lines.Clear();
        if (runtime.Execute(program) is ErrorObj error) throw new InvalidOperationException(error.Message);
        if (lines.Count != 2 || !lines[0].StartsWith("RESULT ") || !lines[1].StartsWith("ELAPSED_MS ")) throw new InvalidOperationException("Unexpected benchmark output.");
        double result = double.Parse(lines[0][7..]), expected = item.Value.GetProperty("expected").GetDouble();
        double tolerance = item.Value.TryGetProperty("relative_tolerance", out var relative) ? Math.Max(item.Value.GetProperty("absolute_tolerance").GetDouble(), Math.Abs(expected) * relative.GetDouble()) : 0;
        if (!double.IsFinite(result) || Math.Abs(result - expected) > tolerance) throw new InvalidOperationException($"Wrong {item.Name} result: {result}");
        return double.Parse(lines[1][11..]);
    }
    for (int i = 0; i < 5; i++) Run();
    var samples = new List<double>();
    for (int i = 0; i < 11; i++) samples.Add(Run());
    var sorted = samples.Order().ToArray();
    rows[item.Name] = new { median_ms = sorted[sorted.Length / 2], samples_ms = samples, source_sha256 = Hash(source), wasm_sha256 = Hash(artifact),
        promoted_functions = program.Metadata.Functions.Count(f => f.PromotedScope), numeric_functions = program.Metadata.Functions.Count(f => f.NumericTable != 0) };
    Console.WriteLine($"{item.Name}: {sorted[sorted.Length / 2]:F4} ms, validated");
}
File.WriteAllText(args[3], JsonSerializer.Serialize(new {
    method = "Unmodified standard benchmark sources; validated checksums; five warmups and eleven measured executions in one process per artifact. Workload timer excludes compilation, initial Wasm instantiation and output. No concurrent tests or benchmarks.",
    fingerprints = new[] { typeof(WasmCompiler).Assembly.Location, typeof(WasmtimeEngine).Assembly.Location }.ToDictionary(p => Path.GetFileName(p)!, Hash), results = rows
}, new JsonSerializerOptions { WriteIndented = true }) + "\n");
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
