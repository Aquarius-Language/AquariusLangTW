using AquariusLang.ast;
using AquariusLang.evaluator;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using AquariusLang.VM;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLangVMTesting.VM;

public class CompilerAndMachineTest {
    private static AbstractSyntaxTree Parse(string source) {
        var lexer = Lexer.NewInstance(source);
        var parser = Parser.NewInstance(lexer);
        var tree = parser.ParseAST();
        Assert.Empty(lexer.Errors); Assert.Empty(parser.Errors);
        return tree;
    }
    private static IObject Run(string source) => new VirtualMachine().Execute(new VmCompiler().Compile(source));

    [Fact]
    public void CompilerEmitsInstructionsAndMachineDoesNotReadTheOriginalTree() {
        var tree = Parse("變數 值 = 6; 如果 (值 > 4) { 值 * 7; } 否則 { 0; };");
        var program = new VmCompiler().Compile(tree);
        Assert.Contains(program.Instructions, i => i.Code == OpCode.Declare);
        Assert.Contains(program.Instructions, i => i.Code == OpCode.JumpIfFalse);
        Assert.Contains("Multiply", program.Disassemble());
        foreach (var instruction in program.Instructions.Where(i => i.Code is OpCode.Jump or OpCode.JumpIfFalse))
            Assert.InRange(instruction.Operand, 0, program.Instructions.Count);
        tree.Statements = System.Array.Empty<IStatement>();
        Assert.Equal(42, Assert.IsType<IntegerObj>(new VirtualMachine().Execute(program)).Value);
    }

    [Fact]
    public void FunctionBodiesAreCompiledBeforeExecution() {
        var tree = Parse("變數 加法 = 函式(值) { 值 + 2; }; 加法(40);");
        var function = Assert.IsType<FunctionLiteral>(Assert.IsType<LetStatement>(tree.Statements[0]).Value);
        var program = new VmCompiler().Compile(tree);
        function.Body.Statements = System.Array.Empty<IStatement>();
        Assert.Equal(42, Assert.IsType<IntegerObj>(new VirtualMachine().Execute(program)).Value);
        Assert.Contains("function", program.Disassemble());
    }

    [Fact]
    public void CompiledProgramsCanBeReusedWithFreshState() {
        var program = new VmCompiler().Compile("變數 陣列 = [1]; 陣列[0] = 陣列[0] + 1; 陣列;");
        var machine = new VirtualMachine();
        var first = Assert.IsType<ArrayObj>(machine.Execute(program));
        first.Elements[0] = new IntegerObj(100);
        Assert.Equal(2, Assert.IsType<IntegerObj>(Assert.IsType<ArrayObj>(machine.Execute(program)).Elements[0]).Value);
        var literal = new VmCompiler().Compile("42;");
        Assert.IsType<IntegerObj>(machine.Execute(literal)).Value = 100;
        Assert.Equal(42, Assert.IsType<IntegerObj>(machine.Execute(literal)).Value);
    }

    [Fact]
    public void ReplEvaluationKeepsGlobalsAndPreviouslyCompiledClosures() {
        var environment = AquaEnvironment.NewEnvironment();
        var evaluator = VmEvaluator.NewInstance(new Builtins());
        evaluator.Evaluate("變數 數值 = 10; 變數 增加 = 函式() { 數值++; 回傳 數值; };", environment);
        Assert.Equal(11, Assert.IsType<IntegerObj>(evaluator.Evaluate("增加();", environment)).Value);
        Assert.Equal(12, Assert.IsType<IntegerObj>(evaluator.Evaluate("增加();", environment)).Value);
    }

    [Fact]
    public void DeepRecursionUsesVmFramesInsteadOfTheHostStack() {
        Assert.Equal(0, Assert.IsType<IntegerObj>(Run(@"
            變數 倒數 = 函式(值) { 如果 (值 == 0) { 回傳 0; } 回傳 倒數(值 - 1); };
            倒數(20000);")).Value);
    }

    [Fact]
    public void BreakRestoresOperandStackAndNestedLoopScopes() {
        Assert.Equal(6, Assert.IsType<IntegerObj>(Run(@"
            變數 結果 = 0;
            迴圈 (變數 外 = 0; 外 < 3; 外++) {
                迴圈 (變數 內 = 0; 內 < 5; 內++) {
                    如果 (內 == 2) { 中斷; }
                    結果++;
                }
            }
            結果;")).Value);
    }

    [Fact]
    public void ClosuresCaptureEachLoopIteration() {
        var result = Assert.IsType<ArrayObj>(Run(@"
            變數 函式們 = [0, 0, 0];
            迴圈 (變數 索引 = 0; 索引 < 3; 索引++) {
                變數 區域 = 索引;
                函式們[索引] = 函式() { 回傳 區域; };
            }
            變數 甲 = 函式們[0]; 變數 乙 = 函式們[1]; 變數 丙 = 函式們[2];
            [甲(), 乙(), 丙()];"));
        Assert.Equal(new[] { 0, 1, 2 }, result.Elements.Cast<IntegerObj>().Select(i => i.Value));
    }

    [Theory]
    [InlineData("未知 + 更新();", "Identifier not found")]
    [InlineData("變數 陣列 = [0]; 陣列[5] = 更新();", "out of bounds")]
    [InlineData("{函式() { 0; }: 更新()};", "Unusable as hash key")]
    [InlineData("變數 函 = 函式(甲) { 甲; }; 函();", "expects 1 arguments")]
    public void ErrorsStopBeforeLaterSideEffects(string source, string expected) {
        var environment = AquaEnvironment.NewEnvironment();
        var evaluator = VmEvaluator.NewInstance(new Builtins());
        evaluator.Evaluate("變數 次數 = 0; 變數 更新 = 函式() { 次數++; 回傳 次數; };", environment);
        Assert.Contains(expected, Assert.IsType<ErrorObj>(evaluator.Evaluate(source, environment)).Message);
        Assert.Equal(0, Assert.IsType<IntegerObj>(environment.Get("次數", out _)).Value);
    }

    [Fact]
    public void NativeCallbacksCanReenterAnExecutingVm() {
        var builtins = new Builtins();
        var machine = new VirtualMachine(builtins);
        builtins.BuiltinFuncs["呼叫回呼"] = new BuiltinObj(args => machine.Invoke(args[0], new IntegerObj(7)));
        var program = new VmCompiler().Compile(@"
            變數 偏移 = 10;
            變數 結果 = 呼叫回呼(函式(值) { 偏移++; 回傳 值 + 偏移; });
            [結果, 偏移];");
        var result = Assert.IsType<ArrayObj>(machine.Execute(program));
        Assert.Equal(18, Assert.IsType<IntegerObj>(result.Elements[0]).Value);
        Assert.Equal(11, Assert.IsType<IntegerObj>(result.Elements[1]).Value);
    }

    [Theory]
    [InlineData("變數 = ;")]
    [InlineData("\"unterminated")]
    public void InvalidSourceReportsCompilationErrors(string source) {
        Assert.Throws<VmCompilationException>(() => new VmCompiler().Compile(source));
        Assert.IsType<ErrorObj>(VmEvaluator.NewInstance(new Builtins()).Evaluate(source));
    }

    [Theory]
    [InlineData("變數 值 = 3; 值 += 值++; 值;")]
    [InlineData("變數 加 = 函式(甲) { 函式(乙) { 甲 + 乙; }; }; 加(2)(3);")]
    [InlineData("變數 值 = 0; 假 && (值++ == 1); 值;")]
    [InlineData("1 && 2;")]
    [InlineData("中斷; 42;")]
    [InlineData("如果 (真) { 中斷; 9; } 42;")]
    [InlineData("變數 函 = 函式() { 中斷; 9; }; 函();")]
    [InlineData("回傳 如果 (真) { 42; } 否則 { 0; };")]
    [InlineData("變數 函 = 函式() { 迴圈 (變數 值=0; 值<5; 值++) { 如果 (值==2) { 回傳 值; } } }; 函();")]
    public void ResultsMatchTheTreeInterpreter(string source) {
        var tree = Parse(source);
        var interpreted = AquariusLang.evaluator.Evaluator.NewInstance(new Builtins()).Eval(tree, AquaEnvironment.NewEnvironment());
        var virtualized = new VirtualMachine().Execute(new VmCompiler().Compile(tree));
        Assert.Equal(interpreted?.Type(), virtualized?.Type());
        Assert.Equal(interpreted?.Inspect(), virtualized?.Inspect());
    }
}
