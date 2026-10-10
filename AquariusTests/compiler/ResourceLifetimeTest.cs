using AquariusLang.Compiler;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.Wasm;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusTests.Wasm;

public class ResourceLifetimeTest {
    [Fact] public void CallbackReentryRetainsNativeArgumentsAndReleasesCompletedExecutions() {
        var resource = new ModuleObj(AquaEnvironment.NewEnvironment());
        var environment = AquaEnvironment.NewEnvironment();environment.Create("resource",resource);
        var builtins = new Builtins();var evaluator = CompiledEvaluator.NewInstance(builtins);
        builtins.DefineFunction(new BuiltinObj(args => {
            environment.Set("resource",RepeatedPrimitives.NULL);
            Assert.Contains(resource._Environment,WasmRuntime.Reachable(Array.Empty<IObject>()));
            return evaluator.Invoke(args[1],args[0]);
        }),englishName:"hold");
        builtins.DefineFunction(new BuiltinObj(args => {
            Assert.Contains(resource._Environment,WasmRuntime.Reachable(Array.Empty<IObject>()));
            return RepeatedPrimitives.TRUE;
        }),englishName:"check");
        Assert.IsType<BooleanObj>(evaluator.Evaluate("hold(resource,函式(local){check(local);});",environment));
        Assert.DoesNotContain(resource._Environment,WasmRuntime.Reachable(Array.Empty<IObject>()));
    }
    [Fact] public void ExtractedNativeMethodRetainsOwnerAndCyclesTerminate() {
        var owner=AquaEnvironment.NewEnvironment();
        var method=new BuiltinObj(_=>RepeatedPrimitives.NULL){RetainedEnvironment=owner};
        var cycle=new ArrayObj(new IObject[2]);cycle.Elements[0]=cycle;cycle.Elements[1]=method;
        Assert.Contains(owner,WasmRuntime.Reachable(new[]{cycle}));
    }
    [Fact] public void FailedExecutionsReleaseTheirRoots() {
        var environment=AquaEnvironment.NewEnvironment();
        var builtins=new Builtins();builtins.DefineFunction(new BuiltinObj(_=>new ErrorObj("denied")),englishName:"fail");
        Assert.IsType<ErrorObj>(CompiledEvaluator.NewInstance(builtins).Evaluate("fail();",environment));
        Assert.DoesNotContain(environment,WasmRuntime.Reachable(Array.Empty<IObject>()));
    }
}
