using AquariusLang.ast;
using AquariusLang.Cli;
using AquariusLang.lexer;
using AquariusLang.parser;
using AquariusLang.Compiler;
using AquariusLang.Wasm;

namespace AquariusTests.Compiler;

/// <summary>The lowering compiler and WebAssembly emitter, and the compiled programs' behavior on the native runtime.</summary>
public class CompilerAndMachineTest {
    private static AbstractSyntaxTree Parse(string source) {
        var lexer = Lexer.NewInstance(source);
        var parser = Parser.NewInstance(lexer);
        var tree = parser.ParseAST();
        Assert.Empty(lexer.Errors); Assert.Empty(parser.Errors);
        return tree;
    }

    [RuntimeFact]
    public void CompilerEmitsInstructionsAndMachineDoesNotReadTheOriginalTree() {
        var tree = Parse("變數 值 = 6; 如果 (值 > 4) { 值 * 7; } 否則 { 0; };");
        var program = new LoweringCompiler().Compile(tree);
        Assert.Contains(program.Instructions, i => i.Code == IrOperation.Declare);
        Assert.Contains(program.Instructions, i => i.Code == IrOperation.JumpIfFalse);
        Assert.Contains("Multiply", program.Disassemble());
        foreach (var instruction in program.Instructions.Where(i => i.Code is IrOperation.Jump or IrOperation.JumpIfFalse))
            Assert.InRange(instruction.Operand, 0, program.Instructions.Count);
        tree.Statements = System.Array.Empty<IStatement>();
        Assert.Equal("42\n", WasmTestHost.Run(new WasmCompiler().Compile(program)).Succeeded().Output);
    }

    [RuntimeFact]
    public void FunctionBodiesAreCompiledBeforeExecution() {
        var tree = Parse("變數 加法 = 函式(值) { 值 + 2; }; 加法(40);");
        var function = Assert.IsType<FunctionLiteral>(Assert.IsType<LetStatement>(tree.Statements[0]).Value);
        var program = new LoweringCompiler().Compile(tree);
        function.Body.Statements = System.Array.Empty<IStatement>();
        Assert.Equal("42\n", WasmTestHost.Run(new WasmCompiler().Compile(program)).Succeeded().Output);
        Assert.Contains("function", program.Disassemble());
    }

    [RuntimeFact]
    public void CompiledProgramsCanBeReusedWithFreshState() {
        // Each run of a compiled program starts from the program's own initial state. (The C# test also mutated the
        // returned C# objects between runs; values now live in the runtime, not in C#.)
        var program = new WasmCompiler().Compile(new LoweringCompiler().Compile("變數 陣列 = [1]; 陣列[0] = 陣列[0] + 1; 陣列;"));
        Assert.Equal("[2]\n", WasmTestHost.Run(program).Succeeded().Output);
        Assert.Equal("[2]\n", WasmTestHost.Run(program).Succeeded().Output);
        var literal = new WasmCompiler().Compile("42;");
        Assert.Equal("42\n", WasmTestHost.Run(literal).Succeeded().Output);
        Assert.Equal("42\n", WasmTestHost.Run(literal).Succeeded().Output);
    }

    [RuntimeFact]
    public void ReplEvaluationKeepsGlobalsAndPreviouslyCompiledClosures() {
        // aqua repl replays the session: later inputs see earlier globals and closures.
        var output = new StringWriter();
        Assert.Equal(0, Repl.Run(new StringReader("變數 數值 = 10; 變數 增加 = 函式() { 數值++; 回傳 數值; };\n增加();\n增加();\n"), output));
        var shown = output.ToString().Replace("\r\n", "\n").Split('\n').Select(line => line.Replace(Repl.Prompt, "")).Where(line => line.Length > 0).Skip(1).ToArray();
        Assert.Equal(new[] { "11", "12" }, shown);
    }

    [RuntimeFact]
    public void DeepRecursionUsesCompiledContinuationsInsteadOfTheHostStack() {
        Assert.Equal(new TestValue("integer", "0"), WasmTestHost.Eval(@"
            變數 倒數 = 函式(值) { 如果 (值 == 0) { 回傳 0; } 回傳 倒數(值 - 1); };
            倒數(20000);"));
    }

    [RuntimeFact]
    public void BreakRestoresOperandStackAndNestedLoopScopes() {
        Assert.Equal(new TestValue("integer", "6"), WasmTestHost.Eval(@"
            變數 結果 = 0;
            迴圈 (變數 外 = 0; 外 < 3; 外++) {
                迴圈 (變數 內 = 0; 內 < 5; 內++) {
                    如果 (內 == 2) { 中斷; }
                    結果++;
                }
            }
            結果;"));
    }

    [RuntimeFact]
    public void ClosuresCaptureEachLoopIteration() {
        Assert.Equal(new TestValue("array", "[0, 1, 2]"), WasmTestHost.Eval(@"
            變數 函式們 = [0, 0, 0];
            迴圈 (變數 索引 = 0; 索引 < 3; 索引++) {
                變數 區域 = 索引;
                函式們[索引] = 函式() { 回傳 區域; };
            }
            變數 甲 = 函式們[0]; 變數 乙 = 函式們[1]; 變數 丙 = 函式們[2];
            [甲(), 乙(), 丙()];"));
    }

    [RuntimeTheory]
    [InlineData("未知 + 更新();", "Identifier not found")]
    [InlineData("變數 陣列 = [0]; 陣列[5] = 更新();", "out of bounds")]
    [InlineData("{函式() { 0; }: 更新()};", "Unusable as hash key")]
    [InlineData("變數 函 = 函式(甲) { 甲; }; 函();", "expects 1 arguments")]
    public void ErrorsStopBeforeLaterSideEffects(string source, string expected) {
        // 更新 prints when it runs, so its side effect would show in the output.
        var run = WasmTestHost.Run("變數 次數 = 0; 變數 更新 = 函式() { 次數++; 印出(\"更新\"); 回傳 次數; };\n" + source + "\n印出(\"after\");");
        Assert.Equal(1, run.ExitCode);
        Assert.Single(run.Lines);
        Assert.StartsWith("ERROR: ", run.Lines[0]);
        Assert.Contains(expected, run.Lines[0]);
    }

    [RuntimeFact]
    public void HostCallsCanReenterTheExecutingRuntime() {
        // The C# test made a host builtin call back into the guest. Natively, 匯入 is such a host call: the host runs the
        // imported module's guest code while the importing expression's operands wait on the guest stack.
        var run = WasmTestHost.RunModules(new Dictionary<string, string> {
            ["main.aqua"] = "變數 偏移 = 10; 變數 結果 = 偏移 + 匯入(\"回呼.aqua\").值 + 偏移++; [結果, 偏移];",
            ["回呼.aqua"] = "變數 遞迴 = 函式(n) { 如果 (n == 0) { 回傳 7; } 回傳 遞迴(n - 1); }; 變數 值 = 遞迴(500) + 1;",
        }, "main.aqua").Succeeded();
        Assert.Equal("[28, 11]\n", run.Output);
    }

    [RuntimeTheory]
    [InlineData("變數 = ;")]
    [InlineData("\"unterminated")]
    public void InvalidSourceReportsCompilationErrors(string source) {
        Assert.Throws<CompilationException>(() => new LoweringCompiler().Compile(source));
        Assert.True(WasmTestHost.Eval(source).IsError);
    }

    [RuntimeFact]
    public void ExecutionPreservesLanguageSemantics() {
        // Whole top-level programs (a top-level 中斷 behaves differently from one in a function body), as before.
        (string Source, TestValue Expected)[] cases = [
            ("變數 值 = 3; 值 += 值++; 值;", new("integer", "7")),
            ("變數 加 = 函式(甲) { 函式(乙) { 甲 + 乙; }; }; 加(2)(3);", new("integer", "5")),
            ("變數 值 = 0; 假 && (值++ == 1); 值;", new("integer", "0")),
            ("1 && 2;", new("error", "ERROR: Unknown operator: INTEGER && INTEGER")),
            ("中斷; 42;", new("integer", "42")),
            ("如果 (真) { 中斷; 9; } 42;", new("integer", "42")),
            ("變數 函 = 函式() { 中斷; 9; }; 函();", new("null", "中斷")),
            ("變數 函 = 函式() { 迴圈 (變數 值=0; 值<5; 值++) { 如果 (值==2) { 回傳 值; } } }; 函();", new("integer", "2")),
        ];
        var values = AquariusTests.Wasm.EmitterTiers.RunAll(cases.Select(c => c.Source).ToArray(), optimize: true);
        for (int i = 0; i < cases.Length; i++) Assert.True(cases[i].Expected == values[i], $"{cases[i].Source}: expected {cases[i].Expected}, got {values[i]}");
        Assert.Equal("42\n", WasmTestHost.Output("回傳 如果 (真) { 42; } 否則 { 0; };"));
    }
}
