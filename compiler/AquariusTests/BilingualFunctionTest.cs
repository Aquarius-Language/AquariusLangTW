using AquariusLang.ast;
using AquariusLang.lexer;
using AquariusLang.parser;
using AquariusLang.Compiler;
using AquariusLang.Wasm;

namespace AquariusTests;

/// <summary>Two-name (Chinese and English) function declarations: one closure, either name, shadowing and modules.</summary>
public class BilingualFunctionTest {
    private static readonly (string Source, string Expected)[] Names = [
        ("變數 加法, add = 函式(甲,乙){甲+乙;};[加法(2,3),add(4,5)];", "[5, 9]"),
        ("變數 add, 加法 = 函式(甲,乙){甲+乙;};[加法(2,3),add(4,5)];", "[5, 9]"),
        ("變數 加法 = 函式(甲,乙){甲+乙;};加法(2,3);", "5"),
        ("變數 add = 函式(甲,乙){甲+乙;};add(2,3);", "5"),
        ("變數 階乘, factorial=函式(n){如果(n<2){回傳 1;}n*階乘(n-1);};factorial(6);", "720"),
        ("變數 階乘, factorial=函式(n){如果(n<2){回傳 1;}n*factorial(n-1);};階乘(6);", "720"),
        ("變數 製造, make=函式(){變數 n=0;變數 計數,count=函式(){n++;n;};[計數,count];};變數 a=make();變數 b=製造();[a[0](),a[1](),b[1]()];", "[1, 2, 1]"),
        ("變數 套用,apply=函式(f,n){f(n);};變數 加一,increment=函式(n){n+1;};[apply(加一,2),套用(increment,4)];", "[3, 5]"),
        ("變數 中文,english=函式(){42;};變數 保存=english;中文=函式(){7;};[中文(),english(),保存()];", "[7, 42, 42]"),
        ("變數 中文,english=函式(){42;};變數 test=函式(){變數 中文,english=函式(){7;};english();};[test(),中文(),english()];", "[7, 42, 42]"),
        ("變數 函式集=[];迴圈(變數 i=0;i<3;i++){變數 local=i;變數 中文,english=函式(){local;};函式集=加入(函式集,[中文,english]);};[函式集[0][0](),函式集[1][1](),函式集[2][0]()];", "[0, 1, 2]"),
        ("變數 長度,len=函式(){42;};[長度(),len()];", "[42, 42]"),
        ("變數 f=函式(len){len();};變數 長度,len=函式(){7;};f(長度);", "7"),
    ];

    [RuntimeFact] public void NamesWorkThroughCompilation() =>
        LanguageCases.Expect(Names.Select(n => (n.Source, n.Expected.StartsWith('[') ? "array" : "integer", n.Expected)).ToArray());

    [RuntimeFact] public void NamesWorkThroughASerializedLoweredProgram() {
        // Lowered IR -> Wasm bytes -> reloaded program, run by the native runtime: one line per case.
        string source = string.Concat(Names.Select(n => $"印出((函式() {{\n{n.Source}\n}})());\n"));
        var wasm = new WasmCompiler().Compile(new LoweringCompiler().Compile(source));
        var reloaded = WasmProgram.Load(wasm.Bytes.ToArray());
        var run = WasmTestHost.Run(reloaded).Succeeded();
        Assert.Equal(Names.Select(n => n.Expected), run.Lines);
    }

    [Fact] public void AstKeepsBothIdentifiersAndLoweringCreatesExactlyOneClosure() {
        var parser = Parser.NewInstance(Lexer.NewInstance("變數 加法, add = 函式(n){n+1;};[加法,add];"));
        var tree = parser.ParseAST(); Assert.Empty(parser.Errors);
        var declaration = Assert.IsType<LetStatement>(tree.Statements[0]);
        Assert.Equal("加法", declaration.Name.Value); Assert.Equal("add", declaration.Alias!.Value);
        Assert.Contains("加法, add", declaration.String());
        var code = new LoweringCompiler().Compile(tree);
        Assert.Single(code.Instructions.Where(i => i.Code == IrOperation.Closure));
    }

    [RuntimeFact] public void BothNamesHoldTheSameFunctionObject() => LanguageCases.Expect(
        ("變數 加法, add = 函式(n){n+1;};[加法 == add, add == 加法, 加法(1), add(2)];", "array", "[真, 真, 2, 3]"),
        // Distinct closures of the same literal stay distinct; reassigning one name does not rebind the other.
        ("變數 make=函式(){函式(){1;};};變數 中文,english=函式(){2;};變數 old=中文;english=make();[make()==make(),中文==old,english==中文];",
            "array", "[假, 真, 假]"));

    [Theory]
    [InlineData("變數 同名,同名=函式(){};")][InlineData("變數 中文,english=42;")]
    [InlineData("變數 中文,english,third=函式(){};")][InlineData("變數 中文,=函式(){};")]
    [InlineData("變數 中文,函式=函式(){};")][InlineData("變數 中文,english;")]
    public void MalformedDeclarationsProduceCompilationErrors(string source) =>
        Assert.Throws<CompilationException>(() => new LoweringCompiler().Compile(source));

    [RuntimeTheory]
    [InlineData("測試")][InlineData("test")]
    public void FailedFunctionBodiesStopBeforeLaterEffectsForEitherName(string name) {
        var run = WasmTestHost.Run($"變數 值=0;變數 測試,test=函式(){{未知;}};印出(\"before\", 值);{name}();值=1;印出(\"after\", 值);");
        Assert.Equal(1, run.ExitCode);
        Assert.Equal("before 0\nERROR: Identifier not found: 未知\n", run.Output);
    }

    [RuntimeFact] public void ModuleAliasesTakePrecedenceOverBuiltinsAndMissingMembersStayMissing() {
        var modules = new Dictionary<string, string> {
            ["lib.aqua"] = "變數 長度,len=函式(){42;};",
            ["main.aqua"] = "變數 lib=匯入(\"lib.aqua\");印出([lib.長度(),lib.len(),len([1]),長度(\"ab\")]);lib.print();印出(\"after\");",
        };
        var run = WasmTestHost.RunModules(modules, "main.aqua");
        Assert.Equal(1, run.ExitCode);
        Assert.Equal("[42, 42, 1, 2]", run.Lines[0]);
        Assert.StartsWith("ERROR: ", run.Lines[1]);
        Assert.Contains("member not found", run.Lines[1]);
        Assert.Equal(2, run.Lines.Count);
    }
}
