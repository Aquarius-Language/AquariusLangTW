using AquariusLang.ast;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using AquariusLang.runtime;
using AquariusLang.Compiler;
using AquariusLang.Wasm;
using AquariusLang.Desktop;
using Xunit;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusTests;

public class BilingualFunctionTest {
    [Theory]
    [InlineData("變數 加法, add = 函式(甲,乙){甲+乙;};[加法(2,3),add(4,5)];", "[5, 9]")]
    [InlineData("變數 add, 加法 = 函式(甲,乙){甲+乙;};[加法(2,3),add(4,5)];", "[5, 9]")]
    [InlineData("變數 加法 = 函式(甲,乙){甲+乙;};加法(2,3);", "5")]
    [InlineData("變數 add = 函式(甲,乙){甲+乙;};add(2,3);", "5")]
    [InlineData("變數 階乘, factorial=函式(n){如果(n<2){回傳 1;}n*階乘(n-1);};factorial(6);", "720")]
    [InlineData("變數 階乘, factorial=函式(n){如果(n<2){回傳 1;}n*factorial(n-1);};階乘(6);", "720")]
    [InlineData("變數 製造, make=函式(){變數 n=0;變數 計數,count=函式(){n++;n;};[計數,count];};變數 a=make();變數 b=製造();[a[0](),a[1](),b[1]()];", "[1, 2, 1]")]
    [InlineData("變數 套用,apply=函式(f,n){f(n);};變數 加一,increment=函式(n){n+1;};[apply(加一,2),套用(increment,4)];", "[3, 5]")]
    [InlineData("變數 中文,english=函式(){42;};變數 保存=english;中文=函式(){7;};[中文(),english(),保存()];", "[7, 42, 42]")]
    [InlineData("變數 中文,english=函式(){42;};變數 test=函式(){變數 中文,english=函式(){7;};english();};[test(),中文(),english()];", "[7, 42, 42]")]
    [InlineData("變數 函式集=[];迴圈(變數 i=0;i<3;i++){變數 local=i;變數 中文,english=函式(){local;};函式集=加入(函式集,[中文,english]);};[函式集[0][0](),函式集[1][1](),函式集[2][0]()];", "[0, 1, 2]")]
    [InlineData("變數 長度,len=函式(){42;};[長度(),len()];", "[42, 42]")]
    [InlineData("變數 f=函式(len){len();};變數 長度,len=函式(){7;};f(長度);", "7")]
    public void NamesWorkThroughCompilationAndSerializedLoweredProgram(string source, string expected) {
        using var builtins = new DesktopBuiltins();
        var code = new LoweringCompiler().Compile(source); var vm = new CompiledRuntime(builtins);
        Assert.Equal(expected, vm.Execute(code).Inspect());
        var wasm = new WasmCompiler().Compile(code);
        Assert.Equal(expected, new WasmRuntime(builtins).Execute(WasmProgram.Load(wasm.Bytes.ToArray())).Inspect());
    }

    [Fact] public void AstKeepsBothIdentifiersAndVmCreatesExactlyOneClosure() {
        var parser = Parser.NewInstance(Lexer.NewInstance("變數 加法, add = 函式(n){n+1;};[加法,add];"));
        var tree = parser.ParseAST(); Assert.Empty(parser.Errors);
        var declaration = Assert.IsType<LetStatement>(tree.Statements[0]);
        Assert.Equal("加法", declaration.Name.Value); Assert.Equal("add", declaration.Alias!.Value);
        Assert.Contains("加法, add", declaration.String());
        var code = new LoweringCompiler().Compile(tree);
        Assert.Single(code.Instructions.Where(i => i.Code == IrOperation.Closure));
        var values = Assert.IsType<ArrayObj>(new CompiledRuntime().Execute(code)).Elements;
        Assert.Same(values[0], values[1]);
        var wasm = new WasmCompiler().Compile(code);
        values = Assert.IsType<ArrayObj>(new WasmRuntime().Execute(WasmProgram.Load(wasm.Bytes.ToArray()))).Elements;
        Assert.Same(values[0], values[1]);
    }

    [Theory]
    [InlineData("變數 同名,同名=函式(){};")][InlineData("變數 中文,english=42;")]
    [InlineData("變數 中文,english,third=函式(){};")][InlineData("變數 中文,=函式(){};")]
    [InlineData("變數 中文,函式=函式(){};")][InlineData("變數 中文,english;")]
    public void MalformedDeclarationsProduceCompilationErrors(string source) =>
        Assert.Throws<CompilationException>(() => new LoweringCompiler().Compile(source));

    [Fact] public void FailedFunctionBodiesStopBeforeLaterEffectsForEitherName() {
        foreach (string name in new[] { "測試", "test" }) {
            var env = AquaEnvironment.NewEnvironment();
            var result = new CompiledRuntime().Execute(new LoweringCompiler().Compile($"變數 值=0;變數 測試,test=函式(){{未知;}};{name}();值=1;"), env);
            Assert.IsType<ErrorObj>(result); Assert.Equal(0, Assert.IsType<IntegerObj>(env.Get("值", out _)).Value);
        }
    }

    [Fact] public void ModuleAliasesTakePrecedenceOverBuiltinsAndMissingMembersStayMissing() {
        using var builtins = new DesktopBuiltins(); var vm = new CompiledRuntime(builtins);
        var module = AquaEnvironment.NewEnvironment();
        vm.Execute(new LoweringCompiler().Compile("變數 長度,len=函式(){42;};"), module);
        var caller = AquaEnvironment.NewEnvironment(); caller.Create("lib", new ModuleObj(module));
        Assert.Equal("[42, 42]", vm.Execute(new LoweringCompiler().Compile("[lib.長度(),lib.len()];"), caller).Inspect());
        Assert.Contains("Module member not found", Assert.IsType<ErrorObj>(vm.Execute(new LoweringCompiler().Compile("lib.print();"), caller)).Message);
    }

    [Theory]
    [InlineData("中文", "english")][InlineData("中文", null)][InlineData(null, "english")]
    public void HostsCanRegisterEitherNameOrBoth(string? chinese, string? english) {
        var env = AquaEnvironment.NewEnvironment(); var builtins = new Builtins();
        var function = new BuiltinObj(args => new IntegerObj(42));
        FunctionRegistration.Define(env, function, chinese, english); builtins.DefineFunction(function, chinese, english);
        foreach (string name in FunctionRegistration.Names(chinese, english)) {
            Assert.Same(function, env.GetOwned(name)); Assert.Same(function, builtins.BuiltinFuncs[name]);
            Assert.Equal(42, Assert.IsType<IntegerObj>(new CompiledRuntime(builtins).Execute(new LoweringCompiler().Compile(name + "();"))).Value);
        }
    }

    [Theory]
    [InlineData(null, null)][InlineData("", "english")][InlineData("中文", "bad name")]
    [InlineData("函式", "english")][InlineData("中文", "中文")][InlineData("中文", "name#comment")]
    public void InvalidHostNamesAreRejectedAtomically(string? chinese, string? english) {
        var env = AquaEnvironment.NewEnvironment(); var registry = new Dictionary<string, BuiltinObj>();
        var function = new BuiltinObj(_ => RepeatedPrimitives.NULL);
        Assert.Throws<ArgumentException>(() => FunctionRegistration.Define(env, function, chinese, english));
        Assert.Throws<ArgumentException>(() => FunctionRegistration.Define(registry, function, chinese, english));
        Assert.Empty(env.OwnedBindings); Assert.Empty(registry);
    }

    [Fact] public void HostNameCollisionsDoNotPartiallyRegisterOrOverwrite() {
        var env = AquaEnvironment.NewEnvironment(); var existing = new IntegerObj(7); env.Create("taken", existing);
        var function = new BuiltinObj(_ => RepeatedPrimitives.NULL);
        Assert.Throws<ArgumentException>(() => FunctionRegistration.Define(env, function, "中文", "taken"));
        Assert.False(env.Owns("中文")); Assert.Same(existing, env.GetOwned("taken"));
        Assert.Throws<ArgumentException>(() => FunctionRegistration.Define(env, existing, "中文"));
        var registry = new Dictionary<string, BuiltinObj> { ["taken"] = function };
        Assert.Throws<ArgumentException>(() => FunctionRegistration.Define(registry, function, "中文", "taken")); Assert.Single(registry);
    }



    [Fact] public void LibraryReloadReplacesBothAliasesAndRejectsUnrelatedGroupsAtomically() {
        var env = AquaEnvironment.NewEnvironment();
        var first = new BuiltinObj(_ => new IntegerObj(1)); var second = new BuiltinObj(_ => new IntegerObj(2));
        FunctionRegistration.Define(env, first, "中文", "english");
        FunctionRegistration.Replace(env, second, "中文", "english");
        Assert.Same(second, env.GetOwned("中文")); Assert.Same(second, env.GetOwned("english"));
        env.Create("unrelated", first);
        Assert.Throws<ArgumentException>(() => FunctionRegistration.Replace(env, first, "中文", "unrelated"));
        Assert.Same(second, env.GetOwned("中文")); Assert.Same(first, env.GetOwned("unrelated"));
        Assert.Throws<ArgumentException>(() => FunctionRegistration.Replace(env, first, "中文", "missing"));
        Assert.False(env.Owns("missing"));
    }
}

/// <summary>Translate known library member identifiers; strings and comments are untouched.</summary>
internal static class BilingualTestSource {
    internal static string Chinese(string source) {
        var lexer = Lexer.NewInstance(source); var replacements = new List<(int Start, int Length, string Name)>();
        AquariusLang.token.Token previous = default, token;
        do {
            token = lexer.NextToken();
            if (previous.Type == AquariusLang.token.TokenType.DOT && token.Type == AquariusLang.token.TokenType.IDENT &&
                LibraryCatalog.TraditionalChinese(token.Literal) is string name)
                replacements.Add((token.Start, token.Length, name));
            previous = token;
        } while (token.Type != AquariusLang.token.TokenType.EOF);
        foreach (var replacement in replacements.AsEnumerable().Reverse())
            source = source.Remove(replacement.Start, replacement.Length).Insert(replacement.Start, replacement.Name);
        return source;
    }
}
