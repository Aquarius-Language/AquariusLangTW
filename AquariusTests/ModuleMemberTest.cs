using AquariusLang.Compiler;
using AquariusLang.runtime;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusTests;

public class ModuleMemberTest {
    private static IObject Evaluate(string source, AquaEnvironment environment) {
        var parser = Parser.NewInstance(Lexer.NewInstance(source));
        var tree = parser.ParseAST(); Assert.Empty(parser.Errors);
        return CompiledEvaluator.NewInstance(new Builtins()).Eval(tree, environment);
    }
    [Fact]
    public void ModuleCallsEvaluateArgumentsInCallerScopeAndFunctionsInModuleScope() {
        var moduleEnv = AquaEnvironment.NewEnvironment();
        Evaluate("變數 偏移 = 10; 變數 加法 = 函式(值) { 回傳 值 + 偏移; };", moduleEnv);
        var caller = AquaEnvironment.NewEnvironment(); caller.Create("模組", new ModuleObj(moduleEnv));
        var value = Evaluate("變數 偏移 = 100; 變數 值 = 7; 模組.加法(值 + 偏移);", caller);
        Assert.Equal(117, Assert.IsType<IntegerObj>(value).Value);
    }
    [Fact]
    public void MembersExposeConstantsAndMissingMembersReturnLanguageErrors() {
        var module = AquaEnvironment.NewEnvironment(); module.Create("常數", new IntegerObj(42));
        var caller = AquaEnvironment.NewEnvironment(); caller.Create("模組", new ModuleObj(module));
        Assert.Equal(42, Assert.IsType<IntegerObj>(Evaluate("模組.常數;", caller)).Value);
        Assert.Contains("member not found", Assert.IsType<ErrorObj>(Evaluate("模組.不存在;", caller)).Message);
        Assert.IsType<ErrorObj>(Evaluate("變數 數值 = 3; 數值.常數;", caller));
    }
    [Fact]
    public void ArgumentErrorsPropagateAndModuleFunctionsCanBeAssigned() {
        var module = AquaEnvironment.NewEnvironment();
        module.Create("原值", new BuiltinObj(args => args[0]));
        var caller = AquaEnvironment.NewEnvironment(); caller.Create("模組", new ModuleObj(module));
        Assert.Equal(8, Assert.IsType<IntegerObj>(Evaluate("變數 呼叫 = 模組.原值; 呼叫(8);", caller)).Value);
        Assert.IsType<ErrorObj>(Evaluate("模組.原值(不存在);", caller));
    }
    [Fact]
    public void NativeBooleanResultsAndStringValuesDriveConditionsCorrectly() {
        var module = AquaEnvironment.NewEnvironment();
        module.Create("關閉", new BuiltinObj(_ => new BooleanObj(false)));
        module.Create("空字串", new BuiltinObj(_ => new StringObj("")));
        var caller = AquaEnvironment.NewEnvironment(); caller.Create("模組", new ModuleObj(module));
        Assert.True(Assert.IsType<BooleanObj>(Evaluate("!模組.關閉();", caller)).Value);
        Assert.Equal(2, Assert.IsType<IntegerObj>(Evaluate("如果 (模組.關閉()) { 1; } 否則 { 2; };", caller)).Value);
        Assert.True(Assert.IsType<BooleanObj>(Evaluate("模組.關閉() == 假;", caller)).Value);
        Assert.True(Assert.IsType<BooleanObj>(Evaluate("模組.空字串() == \"\";", caller)).Value);
    }
    [Fact]
    public void HostModuleMembersAreReadAgainAfterTheHostChangesThem() {
        // The guest caches members of host modules per host era; any host binding change must be seen by the next read.
        var module = AquaEnvironment.NewEnvironment(); int n = 0;
        module.Create("value", new IntegerObj(0));
        module.Create("bump", new BuiltinObj(_ => { module.Set("value", new IntegerObj(++n)); return RepeatedPrimitives.NULL; }));
        module.Create("f", new BuiltinObj(_ => new IntegerObj(1)));
        module.Create("swap", new BuiltinObj(_ => { module.Set("f", new BuiltinObj(_ => new IntegerObj(2))); return RepeatedPrimitives.NULL; }));
        var caller = AquaEnvironment.NewEnvironment(); caller.Create("m", new ModuleObj(module));
        var result = Assert.IsType<ArrayObj>(Evaluate("變數 total=0;迴圈(變數 i=0;i<5;i++){total+=m.value;m.bump();}變數 a=m.f();m.swap();[total,a,m.f(),m.value];", caller));
        Assert.Equal(new[] { 10, 1, 2, 5 }, result.Elements.Select(e => Assert.IsType<IntegerObj>(e).Value));
    }
}
