using System.Diagnostics;
using System.Text;
using AquariusLang.Build;
using AquariusLang.Compiler;
using AquariusLang.Libraries;
using AquariusLang.Wasm;

namespace AquariusTests;

/// <summary>What a program run printed and how it ended.</summary>
internal sealed record RunResult(int ExitCode, string Output, string Error)
{
    public IReadOnlyList<string> Lines => Output.Length == 0 ? [] : Output.TrimEnd('\n').Split('\n');
    public RunResult Succeeded()
    {
        Assert.True(ExitCode == 0, $"exit {ExitCode}\n{Output}\n{Error}");
        return this;
    }
}

/// <summary>The value a program's final expression produced: its <c>型別</c> tag and its display text (as <c>印出</c>
/// shows it). A program stopped by an error gives type "error" and the error's message.</summary>
internal sealed record TestValue(string Type, string Text)
{
    public bool IsError => Type == "error";
    public string Message => Text.StartsWith("ERROR: ", StringComparison.Ordinal) ? Text[7..] : Text;
    public int Integer { get { Assert.Equal("integer", Type); return int.Parse(Text, System.Globalization.CultureInfo.InvariantCulture); } }
    public double Number => double.Parse(Text, System.Globalization.CultureInfo.InvariantCulture);
    public bool Boolean { get { Assert.Equal("boolean", Type); return Text == "真"; } }
    public override string ToString() => $"{Type}: {Text}";
}

/// <summary>Runs compiled programs on this machine's native runtime pack (aquarius / aquarius.exe from
/// .native-build/install/&lt;target&gt;, built by scripts/build-runtime.ps1), the same runtime applications use.</summary>
internal static class WasmTestHost
{
    private static readonly Lazy<string?> launcher = new(() => {
        try { return NativeRuntime.Launcher(); } catch (Exception e) when (e is IOException or PlatformNotSupportedException) { return null; }
    });
    public static bool Available => launcher.Value != null;
    public const string MissingRuntime = "Needs the host runtime pack (scripts/build-runtime.ps1 -Targets windows-x64 or linux-x64).";

    private static readonly string scratch = Path.Combine(Path.GetTempPath(), "aquarius-tests-" + Environment.ProcessId);

    /// <summary>A fresh folder for one test's files.</summary>
    public static string Directory()
    {
        string directory = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    public static RunResult RunWasm(string wasm, IEnumerable<string>? arguments = null, string? entry = null,
        IReadOnlyDictionary<string, string>? environment = null, IEnumerable<string>? libraries = null, int timeoutMs = 120_000, string? input = null)
    {
        var start = NativeRuntime.StartInfo(wasm, entry, arguments, libraries);
        start.RedirectStandardOutput = true; start.RedirectStandardError = true; start.RedirectStandardInput = input != null;
        start.StandardOutputEncoding = Encoding.UTF8; start.StandardErrorEncoding = Encoding.UTF8;
        start.CreateNoWindow = true;
        foreach (var pair in environment ?? new Dictionary<string, string>()) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input != null) { process.StandardInput.Write(input); process.StandardInput.Close(); }
        if (!process.WaitForExit(timeoutMs)) { process.Kill(true); throw new TimeoutException($"{wasm} did not finish in {timeoutMs} ms."); }
        return new RunResult(process.ExitCode, output.GetAwaiter().GetResult().Replace("\r\n", "\n"), error.GetAwaiter().GetResult().Replace("\r\n", "\n"));
    }

    public static RunResult Run(WasmProgram program, IEnumerable<string>? arguments = null, string? entry = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        string wasm = Path.Combine(Directory(), "app.wasm");
        File.WriteAllBytes(wasm, program.Bytes.ToArray());
        return RunWasm(wasm, arguments, entry, environment);
    }

    /// <summary>Compiles one source as main.aqua (with the default libraries) and runs it.</summary>
    public static RunResult Run(string source, IReadOnlyDictionary<string, string>? environment = null) =>
        Run(new WasmCompiler().Compile(source), environment: environment);

    /// <summary>Compiles a set of modules (relative path to source) into one application and runs its entry.</summary>
    public static RunResult RunModules(IReadOnlyDictionary<string, string> modules, string entry, IReadOnlyDictionary<string, byte[]>? assets = null, LibrarySet? libraries = null)
    {
        var lowered = modules.ToDictionary(m => m.Key, m => new LoweringCompiler().Compile(m.Value), StringComparer.OrdinalIgnoreCase);
        return Run(new WasmCompiler(true, libraries).Compile(lowered, entry, assets));
    }

    /// <summary>What a program prints, after checking it succeeded.</summary>
    public static string Output(string source) => Run(source).Succeeded().Output;

    private const string Marker = "@@aquarius-test@@";
    private static string Wrap(string source, int index) =>
        $"印出(\"{Marker}begin{index}\");\n變數 __測試值{index} = (函式() {{\n{source}\n}})();\n印出(\"{Marker}{index}:\" + 型別(__測試值{index}));\n印出(__測試值{index});\n";

    /// <summary>The value of a program's final expression. The program runs as the body of a function, so a final
    /// expression (or a 回傳 / return) is its value.</summary>
    public static TestValue Eval(string source) => EvalMany([source])[0];

    /// <summary>Evaluates many programs in one run of the runtime; when one fails, each runs on its own.</summary>
    public static IReadOnlyList<TestValue> EvalMany(IReadOnlyList<string> sources)
    {
        if (sources.Count > 1) {
            string batch = string.Concat(sources.Select(Wrap));
            try {
                var result = Run(batch);
                if (result.ExitCode == 0) return Parse(result, sources.Count);
            } catch (CompilationException) { }
            return sources.Select(Eval).ToArray();
        }
        RunResult single;
        try { single = Run(Wrap(sources[0], 0)); }
        catch (CompilationException e) { return [new TestValue("error", e.Message)]; }
        if (single.ExitCode == 0) return Parse(single, 1);
        // The error stopped the program: its message is the last thing printed.
        int at = single.Output.LastIndexOf("ERROR: ", StringComparison.Ordinal);
        return [new TestValue("error", (at >= 0 ? single.Output[at..] : single.Output + single.Error).TrimEnd('\n'))];
    }

    private static TestValue[] Parse(RunResult result, int count)
    {
        var values = new TestValue[count];
        var lines = result.Lines;
        for (int i = 0; i < count; i++) {
            int typeLine = -1;
            for (int l = 0; l < lines.Count; l++) if (lines[l].StartsWith($"{Marker}{i}:", StringComparison.Ordinal)) { typeLine = l; break; }
            Assert.True(typeLine >= 0, "No value printed for case " + i + ":\n" + result.Output);
            int end = lines.Count;
            for (int l = typeLine + 1; l < lines.Count; l++) if (lines[l] == $"{Marker}begin{i + 1}") { end = l; break; }
            string type = lines[typeLine][$"{Marker}{i}:".Length..];
            values[i] = new TestValue(type, string.Join('\n', lines.Skip(typeLine + 1).Take(end - typeLine - 1)));
        }
        return values;
    }
}

/// <summary>A fact that runs a program on the native runtime: skipped when the host runtime pack is not built.</summary>
public sealed class RuntimeFactAttribute : FactAttribute
{
    public RuntimeFactAttribute() { if (!WasmTestHost.Available) Skip = WasmTestHost.MissingRuntime; }
}

/// <summary>A theory that runs programs on the native runtime: skipped when the host runtime pack is not built.</summary>
public sealed class RuntimeTheoryAttribute : TheoryAttribute
{
    public RuntimeTheoryAttribute() { if (!WasmTestHost.Available) Skip = WasmTestHost.MissingRuntime; }
}

/// <summary>Native runtime bugs these tests found (the runtime belongs to runtime/, outside the compiler). Each skipped
/// test keeps the correct expectation; remove the skip when the runtime is fixed.</summary>
internal static class RuntimeBug
{
    public const string PortableImportIdentity = "Runtime bug: 匯入 of a portable module (Array/陣列, Hash, String) builds a new module " +
        "object on every call (runtime/guest/runtime.c portable_module), so 匯入(\"Array\")==匯入(\"陣列\") is 假.";
    public const string WorkerRefusals = "Runtime bug: runtime/host/threads.c worker_service answers 1 (\"pending\") for services it refuses, " +
        "so a worker starting or waiting for threads, or calling a captured host function, fails with \"Invalid worker execution status\".";
    public const string ModuleErrorTwice = "Runtime bug: an imported module's runtime error is printed twice (runtime/host/scheduler.c run_module's " +
        "task has no done callback, so task_finish prints it before the importer does).";
    public const string CircularImports = "Runtime bug: a circular import traps the guest (empty ERROR, then WebAssembly traps) instead of a " +
        "language error naming the cycle.";
    public const string FunctionCount = "Runtime bug: runtime/host/application.c does not compare the metadata's function count with the " +
        "program's function table, so such a module runs.";
    public const string DamagedExecutable = "Runtime bug: runtime/platform/desktop/main.c treats an executable whose overlay checksum or " +
        "version is wrong as the bare runtime (usage text, exit 2) instead of reporting a damaged application (exit 1).";
}
