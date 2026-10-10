using AquariusLang.Compiler;
using AquariusLang.runtime;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using Xunit;
using AquaEnvironment=AquariusLang.Object.Environment;

namespace AquariusTests;

public class ArrayAssignmentTest {
    private static IObject Eval(string source) {
        var parser=Parser.NewInstance(Lexer.NewInstance(source));var tree=parser.ParseAST();Assert.Empty(parser.Errors);
        return CompiledEvaluator.NewInstance(new Builtins()).Eval(tree,AquaEnvironment.NewEnvironment());
    }
    [Fact] public void IndexedWritesMutateAliasesAndEvaluateOperandsOnce() {
        var result=Assert.IsType<ArrayObj>(Eval(@"變數 a=[0,0];變數 alias=a;變數 count=0;變數 index=0;
            變數 container=函式(){count++;回傳 a;};container()[index++]=17;[alias[0],count,index];"));
        Assert.Equal(new[]{17,1,1},result.Elements.Select(o=>Assert.IsType<IntegerObj>(o).Value));
    }
    [Fact] public void IndexedWritesSupportNestedArraysAndPropagateValueErrors() {
        Assert.Equal(9,Assert.IsType<IntegerObj>(Eval("變數 a=[[0]];a[0][0]=9;a[0][0];")).Value);
        Assert.Contains("Identifier not found",Assert.IsType<ErrorObj>(Eval("變數 a=[0];a[0]=未知;")).Message);
    }
    [Theory]
    [InlineData("變數 a=[0];a[-1]=9;","bounds")]
    [InlineData("變數 a=[0];a[1]=9;","bounds")]
    [InlineData("變數 a=[0];a[0.5d]=9;","integer")]
    [InlineData("變數 a=\"abc\";a[0]=9;","array")]
    public void InvalidWritesReturnLanguageErrors(string source,string expected)=>Assert.Contains(expected,Assert.IsType<ErrorObj>(Eval(source)).Message);
}
