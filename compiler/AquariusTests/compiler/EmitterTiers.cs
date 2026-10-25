using AquariusLang.ast;
using AquariusLang.Compiler;
using AquariusLang.lexer;
using AquariusLang.parser;
using AquariusLang.Wasm;

namespace AquariusTests.Wasm;

/// <summary>Differential runs of the WebAssembly emitter's tiers on the native runtime: the same program compiled by the
/// generic emitter (<c>new WasmCompiler(false)</c>) and by the optimizing one (typed scalar functions, frame-slot scopes,
/// guarded fast paths) must produce the same value. Programs keep running at top level (not wrapped in a function), as
/// the tests ran them on the C# host, so top-level scopes stay dynamic; the native host's fuel quantum is fixed.</summary>
internal static class EmitterTiers
{
    private const string Marker = "@@aquarius-tier@@";
    private const string Begin = Marker + "begin", Value = Marker + "value";

    /// <summary>The value of each program's final expression statement when it runs at top level, compiled with the
    /// given emitter; a runtime or compile error gives type "error". Each program is a module of an application (a
    /// module's top level is compiled like a main script's) that its entry imports in turn, so a few programs share one
    /// run. An error stops a run, which then restarts after the failed program; groups of programs run in parallel.</summary>
    public static IReadOnlyList<TestValue> RunAll(IReadOnlyList<string> sources, bool optimize)
    {
        var values = new TestValue[sources.Count];
        var modules = new LoweredProgram?[sources.Count];
        for (int i = 0; i < sources.Count; i++) {
            try { modules[i] = new LoweringCompiler().Compile(Instrument(sources[i])); }
            catch (CompilationException e) { values[i] = new TestValue("error", e.Message); }
        }
        const int GroupSize = 12;
        Parallel.For(0, (sources.Count + GroupSize - 1) / GroupSize, group => {
            int next = group * GroupSize, end = Math.Min(sources.Count, next + GroupSize);
            while (true) {
                var pending = Enumerable.Range(next, end - next).Where(i => modules[i] != null).ToList();
                if (pending.Count == 0) break;
                var application = pending.ToDictionary(i => $"case{i}.aqua", i => modules[i]!, StringComparer.OrdinalIgnoreCase);
                // The entry ends with a declaration, so the run prints no final value of its own.
                application["main.aqua"] = new LoweringCompiler().Compile(
                    string.Concat(pending.Select(i => $"印出(\"{Begin}\");印出({i});匯入(\"case{i}.aqua\");\n")) + "變數 __完成 = 0;\n");
                int failed = Collect(WasmTestHost.Run(new WasmCompiler(optimize).Compile(application, "main.aqua")), values);
                if (failed < 0) break;
                next = failed + 1;
            }
        });
        Assert.All(values, Assert.NotNull);
        return values;
    }

    /// <summary>Runs every program with both emitters and asserts the optimizing emitter gives the generic emitter's type
    /// and display text. Returns the generic results.</summary>
    public static IReadOnlyList<TestValue> AssertSame(IReadOnlyList<string> sources)
    {
        IReadOnlyList<TestValue>? generic = null, optimized = null;
        Parallel.Invoke(() => generic = RunAll(sources, false), () => optimized = RunAll(sources, true));
        var mismatches = Enumerable.Range(0, sources.Count).Where(i => generic![i] != optimized![i])
            .Select(i => $"{sources[i]}\n  generic:   {generic![i]}\n  optimized: {optimized![i]}").ToList();
        Assert.True(mismatches.Count == 0, "Optimized emitter differs from the generic emitter:\n" + string.Join('\n', mismatches));
        return generic!;
    }

    /// <summary>Evaluates <paramref name="expression"/> for every case number <c>k</c> from 0 to count - 1, in a loop after
    /// a shared top-level prelude, in one program run (the cases are data, so the program stays small). A runtime error
    /// stops a run: that case's value is the error and the run restarts after it, so keep failing cases few.</summary>
    public static IReadOnlyList<TestValue> EvalCases(string prelude, string expression, int count, bool optimize)
    {
        var values = new TestValue[count];
        int next = 0;
        while (next < count) {
            string source = prelude + $"\n迴圈(變數 k={next};k<{count};k++){{印出(\"{Begin}\");印出(k);變數 __tier值=({expression});" +
                $"印出(\"{Value}\");印出(型別(__tier值));印出(__tier值);}}\n";
            int failed = Collect(WasmTestHost.Run(new WasmCompiler(optimize).Compile(source)), values);
            if (failed < 0) break;
            Assert.True(failed >= next, "The prelude failed.");
            next = failed + 1;
        }
        Assert.All(values, Assert.NotNull);
        return values;
    }

    /// <summary><see cref="EvalCases"/> with both emitters, asserting they agree; <paramref name="describe"/> names a case
    /// in failure messages. Returns the generic emitter's values.</summary>
    public static IReadOnlyList<TestValue> AssertSameCases(string prelude, string expression, int count, Func<int, string> describe)
    {
        IReadOnlyList<TestValue>? generic = null, optimized = null;
        Parallel.Invoke(() => generic = EvalCases(prelude, expression, count, false), () => optimized = EvalCases(prelude, expression, count, true));
        var mismatches = Enumerable.Range(0, count).Where(i => generic![i] != optimized![i])
            .Select(i => $"{describe(i)}: generic {generic![i]}, optimized {optimized![i]}").ToList();
        Assert.True(mismatches.Count == 0, "Optimized emitter differs from the generic emitter:\n" + string.Join('\n', mismatches));
        return generic!;
    }

    /// <summary>Reads the cases a run printed (begin marker, case number, then value marker, type and display text). On
    /// an error the case that was running gets the error message; returns its number, or -1 when the run completed.</summary>
    private static int Collect(RunResult result, TestValue[] values)
    {
        var lines = result.Lines;
        int current = -1;
        for (int l = 0; l < lines.Count; l++) {
            if (lines[l] == Begin) { current = int.Parse(lines[++l]); continue; }
            if (lines[l] != Value) continue;
            int end = l + 2;
            while (end < lines.Count && lines[end] != Begin) end++;
            values[current] = new TestValue(lines[l + 1], string.Join('\n', lines.Skip(l + 2).Take(end - l - 2)));
            current = -1;
            l = end - 1;
        }
        if (result.ExitCode == 0) return -1;
        Assert.True(current >= 0, $"The run failed outside a case:\n{result.Output}{result.Error}");
        int error = result.Output.LastIndexOf("ERROR: ", StringComparison.Ordinal);
        values[current] = new TestValue("error", (error >= 0 ? result.Output[error..] : result.Output + result.Error).TrimEnd('\n'));
        return current;
    }

    /// <summary>Assigns the program's final expression statement to a variable and prints its type and value, so the
    /// rest of the program runs unchanged at top level.</summary>
    private static string Instrument(string source)
    {
        var lexer = Lexer.NewInstance(source);
        var parser = Parser.NewInstance(lexer);
        var tree = parser.ParseAST();
        if (lexer.Errors.Count > 0 || parser.Errors.Count > 0) return source;
        Assert.True(tree.Statements.Length > 0 && tree.Statements[^1] is ExpressionStatement, "The program must end with an expression: " + source);
        int start = ((ExpressionStatement)tree.Statements[^1]).Token.Start;
        string last = source[start..].TrimEnd();
        if (last.EndsWith(';')) last = last[..^1];
        return source[..start] + $"\n變數 __tier值 = ({last});\n印出(\"{Value}\");\n印出(型別(__tier值));\n印出(__tier值);\n";
    }
}
