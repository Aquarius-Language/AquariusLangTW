using AquariusLang.Object;
using Xunit;
using Xunit.Abstractions;

namespace AquariusREPL.runtime; 

public class ScriptRunnerTest {
    /// <summary>
    /// For logging outputs during testing.
    /// </summary>
    private readonly ITestOutputHelper _testOutputHelper;

    public ScriptRunnerTest(ITestOutputHelper testOutputHelper) {
        _testOutputHelper = testOutputHelper;
    }
    
    [Fact]
    public void TestForLoop() {
        IObject evaluated = ScriptRunner.RunFile(Path.Combine(AppContext.BaseDirectory, "examples/for_loop.aqua"));
        Assert.IsType<IntegerObj>(evaluated);
        Assert.Equal(-20, ((IntegerObj)evaluated).Value);
    }

    [Fact]
    public void TestNestedFunc() {
        IObject evaluated = ScriptRunner.RunFile(Path.Combine(AppContext.BaseDirectory, "examples/nested_func.aqua"));
        Assert.IsType<IntegerObj>(evaluated);
        Assert.Equal(59, ((IntegerObj)evaluated).Value);
    }

    [Fact]
    public void TestChineseIncrementExample() {
        IObject evaluated = ScriptRunner.RunFile(Path.Combine(AppContext.BaseDirectory, "examples/increment.aqua"));
        Assert.Equal(7, Assert.IsType<IntegerObj>(evaluated).Value);
    }

    [Fact]
    public void TestIncrementAndPlusEqualExample() {
        string examplePath = Path.Combine(AppContext.BaseDirectory,
            "examples/increment_and_plus_equal.aqua");
        IObject evaluated = ScriptRunner.RunFile(examplePath);
        Assert.True(Assert.IsType<BooleanObj>(evaluated).Value,
            "The ++ and += example reported a failed check. See the VM output for details.");
    }

    [Theory]
    [InlineData("長度", 3)]
    [InlineData("最後一個", 3)]
    public void TestChineseArrayBuiltins(string name, int expected) {
        using DesktopBuiltins builtins = new DesktopBuiltins();
        IObject evaluated = builtins.BuiltinFuncs[name].Fn(new IObject[] {
            new ArrayObj(new IObject[] { new IntegerObj(1), new IntegerObj(2), new IntegerObj(3) })
        });
        Assert.Equal(expected, Assert.IsType<IntegerObj>(evaluated).Value);
    }

    [Fact]
    public void TestNumOperationsCasting() {
        IObject evaluated = ScriptRunner.RunFile(Path.Combine(AppContext.BaseDirectory, "examples/num_operations_casting.aqua"));
        Assert.IsType<ArrayObj>(evaluated);
        ArrayObj evaluatedArr = (ArrayObj)evaluated;
        Assert.True(testArrayObjEquals(evaluatedArr.Elements, new IObject[]{new IntegerObj(-20), new FloatObj(20.38f)}));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void TestModuleImport(bool chinese) {
        using var temp = new BottleTestDirectory();
        string folder = Path.Combine(AppContext.BaseDirectory, "examples/using_modules");
        string source = File.ReadAllText(Path.Combine(folder, "main.aqua"));
        if (chinese) source = source.Replace("moduleA.add", "moduleA.加法").Replace("moduleA.minus", "moduleA.減法");
        temp.Write("module_folder/module.aqua", File.ReadAllText(Path.Combine(folder, "module_folder/module.aqua")));
        IObject evaluated = ScriptRunner.RunFile(temp.Write("main.aqua", source));
        Assert.IsType<ArrayObj>(evaluated);
        ArrayObj evaluatedArr = (ArrayObj)evaluated;
        
        Assert.IsType<IntegerObj>(evaluatedArr.Elements[0]);
        Assert.Equal(8, ((IntegerObj)evaluatedArr.Elements[0]).Value);
        
        Assert.IsType<IntegerObj>(evaluatedArr.Elements[1]);
        Assert.Equal(7, ((IntegerObj)evaluatedArr.Elements[1]).Value);
    }

    [Fact]
    public void TestCheckOSPlatform() {
        IObject evaluated = ScriptRunner.RunFile(Path.Combine(AppContext.BaseDirectory, "examples/check_os_platform.aqua"));
        Assert.IsType<ArrayObj>(evaluated);
        ArrayObj evaluatedArr = (ArrayObj)evaluated;
        
        _testOutputHelper.WriteLine(evaluatedArr.Inspect());
        
        Assert.True(testArrayObjEquals(new IObject[] {
            new BooleanObj(OperatingSystem.IsWindows()),
            new BooleanObj(OperatingSystem.IsLinux()),
            new BooleanObj(OperatingSystem.IsMacOS()),
        }, evaluatedArr.Elements));
    }

    [Fact]
    public void TestExecuteFile() {
        IObject evaluated = ScriptRunner.RunFile(Path.Combine(AppContext.BaseDirectory, "examples/execute_file.aqua"));
        Assert.IsType<BooleanObj>(evaluated);
        _testOutputHelper.WriteLine(evaluated.Inspect());
    }

    [Fact]
    public void TestScriptAndScriptDirPath() {
        string scriptPath = Path.Combine(AppContext.BaseDirectory, "examples/dir_paths/main.aqua");
        string directoryPath = Path.GetDirectoryName(scriptPath)!;
        IObject evaluated = ScriptRunner.RunFile(scriptPath);
        
        Assert.IsType<ArrayObj>(evaluated);
        
        ArrayObj evaluatedArrObj = (ArrayObj)evaluated;
        _testOutputHelper.WriteLine(evaluatedArrObj.Inspect());
        
        IObject[] elements = evaluatedArrObj.Elements;

        Assert.Equal(Path.GetFullPath(directoryPath), Path.GetFullPath(((StringObj)elements[0]).Value));

        Assert.True(Directory.Exists(((StringObj)elements[0]).Value));
        Assert.True(Directory.Exists(((StringObj)elements[1]).Value));
    }

    private bool testArrayObjEquals(IObject[] a, IObject[] b) {
        if (a.Length != b.Length) return false;
        bool same = true;
        for (var i = 0; i < a.Length; i++) {
            if (a[i].GetType() != b[i].GetType()) {
                _testOutputHelper.WriteLine($"Not same type: ${a[i].GetType()}, ${b[i].GetType()}");
            }
            if (a[i].Inspect() != b[i].Inspect()) {
                same = false;
                _testOutputHelper.WriteLine($"Not same value: ${a[i].Inspect()}, ${b[i].Inspect()}");
                break;
            } 
        }

        return same;
    }
}
