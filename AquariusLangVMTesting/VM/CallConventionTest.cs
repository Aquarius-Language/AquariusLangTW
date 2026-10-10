using System.Diagnostics;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.VM;
using Xunit.Abstractions;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLangVMTesting.VM;

public class CallConventionTest {
    private readonly ITestOutputHelper output;
    public CallConventionTest(ITestOutputHelper output) { this.output = output; }

    [Fact]
    public void LegacyNativeArgumentsRemainIndependentAndMayBeRetained() {
        var saved = new List<IObject[]>();
        var builtins = new Builtins();
        builtins.BuiltinFuncs["保存"] = new BuiltinObj(args => { saved.Add(args); return args[0]; });
        var result = new VirtualMachine(builtins).Execute(new VmCompiler().Compile("保存(7); 保存(42);"));
        Assert.Equal(42, Assert.IsType<IntegerObj>(result).Value);
        Assert.NotSame(saved[0], saved[1]);
        Assert.Equal(7, Assert.IsType<IntegerObj>(saved[0][0]).Value);
        saved[0][0] = new IntegerObj(99);
        Assert.Equal(42, Assert.IsType<IntegerObj>(saved[1][0]).Value);
    }

    [Fact]
    public void BorrowedNativeArgumentsSurviveCallbackReentryAndNestedCalls() {
        var builtins = new Builtins();
        var machine = new VirtualMachine(builtins);
        builtins.BuiltinFuncs["回呼"] = BuiltinObj.FromBorrowed(args => {
            var before = args[1];
            var result = machine.Invoke(args[0], before);
            Assert.Same(before, args[1]);
            Assert.Equal(7, Assert.IsType<IntegerObj>(args[1]).Value);
            return result;
        });
        var program = new VmCompiler().Compile("""
            變數 加 = 函式(x,y) { 回傳 x+y; };
            變數 遞迴 = 函式(n) { 如果(n==0){回傳 加(10,20);} 回傳 遞迴(n-1); };
            1 + 回呼(函式(n) { 回傳 n+遞迴(500); }, 加(3,4)) + 4;
            """);
        Assert.Equal(42, Assert.IsType<IntegerObj>(machine.Execute(program)).Value);
    }

    [Fact]
    public void BorrowedNativeCallHandlesLargeArgumentWindowsAndResultAliasing() {
        var builtins = new Builtins();
        builtins.BuiltinFuncs["末值"] = BuiltinObj.FromBorrowed(args => args[^1]);
        var source = "變數 a=[42]; 變數 b=末值(" + string.Join(",", Enumerable.Repeat("1", 256)) + ",a); b[0]=7; a[0];";
        var program = new VmCompiler().Compile(source);
        Assert.Equal(7, Assert.IsType<IntegerObj>(new VirtualMachine(builtins).Execute(program)).Value);
    }

    [Fact]
    public void ReplacingFnDisablesTheBorrowedEntryForBothAliases() {
        var builtins = new Builtins();
        var native = BuiltinObj.FromBorrowed(args => new IntegerObj(0));
        FunctionRegistration.Define(builtins.BuiltinFuncs, native, "數值", "value");
        Assert.Throws<ArgumentNullException>(() => native.Fn = null!);
        Assert.Equal(0, Assert.IsType<IntegerObj>(native.Fn([])).Value);
        native.Fn = args => args[0];
        var program = new VmCompiler().Compile("數值(20)+value(22);");
        Assert.Equal(42, Assert.IsType<IntegerObj>(new VirtualMachine(builtins).Execute(program)).Value);
    }

    [Fact]
    public void CallsPreserveArgumentEvaluationOrderAndSerializedPrograms() {
        var program = new VmCompiler().Compile("""
            變數 次=0;
            變數 更新=函式(){次++;回傳 次;};
            變數 合=函式(x,y,z){回傳 x*100+y*10+z;};
            [合(更新(),更新(),更新()),次];
            """);
        using var bytes = new MemoryStream();
        BytecodeSerializer.Write(program, bytes); bytes.Position = 0;
        var machine = new VirtualMachine();
        foreach (var candidate in new[] { program, BytecodeSerializer.Read(bytes) }) {
            var result = Assert.IsType<ArrayObj>(machine.Execute(candidate));
            Assert.Equal(new[] { 123, 3 }, result.Elements.Cast<IntegerObj>().Select(n => n.Value));
        }
    }

    [Fact]
    public void NativeErrorsStopBeforeSubsequentSideEffects() {
        var builtins = new Builtins();
        builtins.BuiltinFuncs["錯誤"] = BuiltinObj.FromBorrowed(_ => new ErrorObj("native failure"));
        var env = AquaEnvironment.NewEnvironment();
        var result = new VirtualMachine(builtins).Execute(new VmCompiler().Compile("變數 次=0; 錯誤(1); 次++;"), env);
        Assert.Equal("native failure", Assert.IsType<ErrorObj>(result).Message);
        Assert.Equal(0, Assert.IsType<IntegerObj>(env.Get("次", out _)).Value);
    }

    [Fact]
    public void BorrowedCallBenchmark() {
        if (System.Environment.GetEnvironmentVariable("AQUARIUS_VM_BENCHMARKS") != "1") return;
        var program = new VmCompiler().Compile("""
            變數 總=0; 迴圈(變數 i=0;i<20000;i++) {
                總+=native(i,1,2,3); 總+=native(i,1,2,3);
                總+=native(i,1,2,3); 總+=native(i,1,2,3);
            } 總;
            """);
        var machines = new List<VirtualMachine>();
        foreach (bool borrowed in new[] { false, true }) {
            var builtins = new Builtins();
            builtins.BuiltinFuncs["native"] = borrowed ? BuiltinObj.FromBorrowed(args => args[0]) : new BuiltinObj(args => args[0]);
            machines.Add(new VirtualMachine(builtins));
        }
        // Warm both paths and alternate sample order to limit tiering/order bias.
        for (int i = 0; i < 10; i++) foreach (var machine in machines) machine.Execute(program);
        var timings = new[] { new List<double>(), new List<double>() };
        var allocations = new[] { new List<long>(), new List<long>() };
        for (int i = 0; i < 9; i++) {
            foreach (int variant in i % 2 == 0 ? new[] { 0, 1 } : new[] { 1, 0 }) {
                long start = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
                var result = machines[variant].Execute(program); clock.Stop();
                allocations[variant].Add(GC.GetAllocatedBytesForCurrentThread() - start); timings[variant].Add(clock.Elapsed.TotalMilliseconds);
                Assert.Equal(799960000, Assert.IsType<IntegerObj>(result).Value);
            }
        }
        for (int variant = 0; variant < 2; variant++) {
            timings[variant].Sort(); allocations[variant].Sort();
            output.WriteLine($"{(variant == 1 ? "borrowed" : "legacy")}: median {timings[variant][4]:F2} ms, {allocations[variant][4]:N0} bytes allocated");
        }
    }
}
