using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using AquariusLang.lexer;
using AquariusLang.Object;
using AquariusLang.parser;
using AquariusLang.VM;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusREPL.Physics;

[CollectionDefinition("Jolt physics", DisableParallelization = true)]
public sealed class JoltCollection { }

[Collection("Jolt physics")]
public class JoltTest {
    internal const string World = "變數 j=匯入(\"Jolt\");變數 w=j.CreateWorld([0,0,0]);";
    internal static IObject Evaluate(string source, DesktopBuiltins? builtins = null) {
        using var owned = builtins == null ? new DesktopBuiltins() : null;
        var lexer = Lexer.NewInstance(source);
        var parser = Parser.NewInstance(lexer);
        var tree = parser.ParseAST();
        Assert.Empty(lexer.Errors); Assert.Empty(parser.Errors);
        return VmEvaluator.NewInstance(builtins ?? owned!).Eval(tree, AquaEnvironment.NewEnvironment());
    }
    private static double Number(IObject value) => Assert.IsAssignableFrom<INumberObj>(value).GetNumValue();
    private static void NearVector(IObject value, double x, double y, double z, double tolerance = 0.0001) {
        var a = Assert.IsType<ArrayObj>(value).Elements;
        Assert.Equal(3, a.Length);
        foreach (var (actual, expected) in a.Zip(new[] { x, y, z }))
            Assert.InRange(Number(actual), expected - tolerance, expected + tolerance);
    }

    [Fact]
    public void ImportsAreCachedAliasesAndDoNotInitializeNativePhysics() {
        int before = JoltLifetime.WorldCount;
        using var builtins = new DesktopBuiltins();
        var result = Assert.IsType<ArrayObj>(Evaluate("[匯入(\"Jolt\"),匯入(\"JoltPhysics\")];", builtins)).Elements;
        Assert.Same(result[0], result[1]);
        var module = Assert.IsType<ModuleObj>(result[0]);
        Assert.Equal("Jolt Physics", Assert.IsType<StringObj>(module._Environment.Get("Backend", out _)).Value);
        Assert.Equal(4096, Assert.IsType<IntegerObj>(module._Environment.Get("MaxBodies", out _)).Value);
        Assert.Equal(before, JoltLifetime.WorldCount);
    }

    [Fact]
    public void ConstantVelocityAdvancesPositionInMetersWithoutDamping() {
        var values = Assert.IsType<ArrayObj>(Evaluate(World + """
            變數 b=w.CreateSphere(0.5d,[1,-2,3],2);w.SetLinearVelocity(b,[2,-3,4]);
            迴圈(變數 i=0;i<120;i++){w.Step(1.0d/60,1);};
            [w.GetPosition(b),w.GetLinearVelocity(b)];
            """)).Elements;
        NearVector(values[0], 5, -8, 11);
        NearVector(values[1], 2, -3, 4);
    }

    [Theory]
    [InlineData(1, 1)][InlineData(4, 7)]
    public void GravityMatchesVelocityAndSemiImplicitEulerPosition(int substeps, int mass) {
        var values = Assert.IsType<ArrayObj>(Evaluate($$"""
            變數 j=匯入("Jolt");變數 w=j.CreateWorld([0,-9.81d,0]);
            變數 b=w.CreateSphere(0.5d,[0,10,0],{{mass}});
            迴圈(變數 i=0;i<60;i++){w.Step(1.0d/60,{{substeps}});};
            [w.GetPosition(b),w.GetLinearVelocity(b)];
            """)).Elements;
        // v_n = g*n*h; y_n = y_0 + g*h*h*n*(n+1)/2 (semi-implicit Euler).
        double h = 1.0 / (60 * substeps), n = 60 * substeps;
        NearVector(values[0], 0, 10 - 9.81 * h * h * n * (n + 1) / 2, 0, 0.002);
        NearVector(values[1], 0, -9.81, 0, 0.0001);
    }

    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(5)]
    public void ImpulseChangesVelocityByImpulseDividedByExplicitMass(int mass) {
        var values = Assert.IsType<ArrayObj>(Evaluate(World + $$"""
            變數 s=w.CreateSphere(0.5d,[0,0,0],{{mass}});
            變數 b=w.CreateBox([1,2,3],[20,0,0],{{mass}});
            w.AddImpulse(s,[6,2,-4]);w.AddImpulse(b,[6,2,-4]);
            [w.GetLinearVelocity(s),w.GetLinearVelocity(b)];
            """)).Elements;
        foreach (var value in values) NearVector(value, 6.0 / mass, 2.0 / mass, -4.0 / mass);
    }

    [Fact]
    public void ForceIntegratesAccelerationAndIsClearedAfterUpdate() {
        var values = Assert.IsType<ArrayObj>(Evaluate(World + """
            變數 b=w.CreateSphere(0.5d,[0,0,0],2);w.AddForce(b,[4,0,0]);w.Step(0.25d,1);
            變數 first=w.GetLinearVelocity(b);w.Step(0.25d,1);
            [first,w.GetLinearVelocity(b),w.GetPosition(b)];
            """)).Elements;
        NearVector(values[0], 0.5, 0, 0); NearVector(values[1], 0.5, 0, 0);
        NearVector(values[2], 0.25, 0, 0);
    }

    [Fact]
    public void AngularImpulseUsesSphereAndBoxMomentOfInertia() {
        var values = Assert.IsType<ArrayObj>(Evaluate(World + """
            變數 s=w.CreateSphere(0.5d,[0,0,0],2);w.AddAngularImpulse(s,[0,0,1]);
            變數 b=w.CreateBox([1,2,3],[20,0,0],12);w.AddAngularImpulse(b,[0,0,20]);
            [w.GetAngularVelocity(s),w.GetAngularVelocity(b)];
            """)).Elements;
        // Sphere: I=2/5*m*r^2=0.2. Box z: I=m/3*(hx^2+hy^2)=20.
        NearVector(values[0], 0, 0, 5); NearVector(values[1], 0, 0, 1);
    }

    [Fact]
    public void PositionRotationAndAngularVelocityRoundTrip() {
        var values = Assert.IsType<ArrayObj>(Evaluate(World + """
            變數 b=w.CreateBox([1,2,3],[0,0,0],1);w.SetPosition(b,[3,4,5]);
            w.SetRotation(b,[0,0,2,2]);w.SetAngularVelocity(b,[0,0,1]);
            [w.GetPosition(b),w.GetRotation(b),w.GetAngularVelocity(b)];
            """)).Elements;
        NearVector(values[0], 3, 4, 5); NearVector(values[2], 0, 0, 1);
        var q = Assert.IsType<ArrayObj>(values[1]).Elements.Select(Number).ToArray();
        Assert.Equal(4, q.Length); Assert.Equal(0, q[0]); Assert.Equal(0, q[1]);
        Assert.InRange(q[2], Math.Sqrt(0.5) - 0.0001, Math.Sqrt(0.5) + 0.0001);
        Assert.InRange(q[3], Math.Sqrt(0.5) - 0.0001, Math.Sqrt(0.5) + 0.0001);
    }

    [Fact]
    public void SphereAndBoxCollideWithStaticFloorAndSettleAtTheirHalfHeight() {
        var values = Assert.IsType<ArrayObj>(Evaluate("""
            變數 j=匯入("Jolt");變數 w=j.CreateWorld([0,-9.81d,0]);
            變數 floor=w.CreateBox([10,0.5d,10],[0,-0.5d,0],0);
            變數 s=w.CreateSphere(0.5d,[-2,2,0],1);變數 b=w.CreateBox([0.5d,0.5d,0.5d],[2,4,0],2);
            w.OptimizeBroadPhase();迴圈(變數 i=0;i<360;i++){w.Step(1.0d/60,1);};
            [w.GetPosition(s),w.GetPosition(b),w.GetLinearVelocity(s),w.GetLinearVelocity(b),w.GetPosition(floor),w.IsActive(s)];
            """)).Elements;
        NearVector(values[0], -2, 0.5, 0, 0.03); NearVector(values[1], 2, 0.5, 0, 0.03);
        NearVector(values[2], 0, 0, 0, 0.01); NearVector(values[3], 0, 0, 0, 0.01);
        NearVector(values[4], 0, -0.5, 0); Assert.False(Assert.IsType<BooleanObj>(values[5]).Value);
    }

    [Fact]
    public void RestitutionOneBouncesAndDynamicLayersCollideWithEachOther() {
        var values = Assert.IsType<ArrayObj>(Evaluate(World + """
            變數 a=w.CreateSphere(0.5d,[-2,0,0],1);變數 b=w.CreateSphere(0.5d,[2,0,0],1);
            w.SetFriction(a,0);w.SetFriction(b,0);w.SetRestitution(a,1);w.SetRestitution(b,1);
            w.SetLinearVelocity(a,[2,0,0]);w.SetLinearVelocity(b,[-2,0,0]);
            迴圈(變數 i=0;i<60;i++){w.Step(1.0d/60,1);};
            [w.GetLinearVelocity(a),w.GetLinearVelocity(b)];
            """)).Elements;
        NearVector(values[0], -2, 0, 0, 0.02); NearVector(values[1], 2, 0, 0, 0.02);
    }

    [Theory]
    [InlineData("j.CreateWorld();", "Expected 1")]
    [InlineData("j.CreateWorld([0,0]);", "3-element")]
    [InlineData("j.CreateWorld([0,\"bad\",0]);", "finite number")]
    [InlineData("w.Step(0,1);", "seconds")]
    [InlineData("w.Step(-0.1d,1);", "seconds")]
    [InlineData("w.Step(1.1d,1);", "seconds")]
    [InlineData("w.Step(0.01d,0);", "Collision steps")]
    [InlineData("w.Step(0.01d,129);", "Collision steps")]
    [InlineData("w.Step(0.01d,1.5d);", "integer")]
    [InlineData("w.CreateSphere(0,[0,0,0],1);", "radius")]
    [InlineData("w.CreateSphere(-1,[0,0,0],1);", "radius")]
    [InlineData("w.CreateSphere(1,[0,0,0],-1);", "Mass")]
    [InlineData("w.CreateSphere(1,[0,0,0],0.0001d);", "Mass")]
    [InlineData("w.CreateBox([1,0,1],[0,0,0],1);", "half extents")]
    [InlineData("w.CreateBox([-1,1,1],[0,0,0],1);", "half extents")]
    [InlineData("w.GetPosition(1);", "live body")]
    [InlineData("w.SetLinearVelocity(w.CreateSphere(1,[0,0,0],0),[1,0,0]);", "dynamic")]
    [InlineData("w.AddForce(w.CreateSphere(1,[0,0,0],0),[1,0,0]);", "dynamic")]
    [InlineData("w.AddAngularImpulse(w.CreateSphere(1,[0,0,0],0),[1,0,0]);", "dynamic")]
    [InlineData("w.SetRotation(w.CreateSphere(1,[0,0,0],1),[0,0,0,0]);", "nonzero")]
    [InlineData("w.SetRotation(w.CreateSphere(1,[0,0,0],1),[0,0,0]);", "4-element")]
    [InlineData("w.SetRestitution(w.CreateSphere(1,[0,0,0],1),1.1d);", "0..1")]
    [InlineData("w.SetFriction(w.CreateSphere(1,[0,0,0],1),-1);", "0..1")]
    [InlineData("w.SetGravity([0,1000001,0]);", "finite number")]
    public void InvalidArgumentsReturnLanguageErrors(string call, string message) {
        Assert.Contains(message, Assert.IsType<ErrorObj>(Evaluate(World + call)).Message);
        Assert.Equal(0, JoltLifetime.WorldCount);
    }

    [Theory]
    [InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(double.NegativeInfinity)][InlineData(double.MaxValue)]
    public void NonFiniteAndUnrepresentableNumbersNeverReachNativeCode(double invalid) {
        using var runtime = new PhysicsRuntime();
        runtime.TryImport("Jolt", out var module);
        var create = Assert.IsType<BuiltinObj>(module._Environment.Get("CreateWorld", out _));
        var result = create.Fn(new IObject[] { new ArrayObj(new IObject[] { new DoubleObj(invalid), new IntegerObj(0), new IntegerObj(0) }) });
        Assert.IsType<ErrorObj>(result); Assert.Equal(0, JoltLifetime.WorldCount);
    }

    [Theory]
    [InlineData("變數 other=j.CreateWorld([0,0,0]);other.GetPosition(b);", "live body")]
    [InlineData("w.RemoveBody(b);w.GetPosition(b);", "live body")]
    [InlineData("w.RemoveBody(b);變數 replacement=w.CreateSphere(1,[0,0,0],1);w.AddImpulse(b,[1,0,0]);", "live body")]
    [InlineData("w.Dispose();w.GetPosition(b);", "disposed")]
    public void BodiesRejectCrossWorldAccessAndUseAfterRemovalOrDisposal(string call, string message) {
        Assert.Contains(message, Assert.IsType<ErrorObj>(Evaluate(World + "變數 b=w.CreateSphere(1,[0,0,0],1);" + call)).Message);
    }

    [Fact]
    public void RepeatedWorldDisposalAndRemovalUpdateCountsAndPreserveOtherWorlds() {
        var values = Assert.IsType<ArrayObj>(Evaluate(World + """
            變數 other=j.CreateWorld([0,0,0]);變數 b=other.CreateSphere(1,[0,0,0],1);
            w.CreateSphere(1,[0,0,0],1);w.Dispose();w.Dispose();
            other.AddImpulse(b,[3,0,0]);other.Step(0.5d,1);變數 p=other.GetPosition(b);
            other.RemoveBody(b);[p,other.GetBodyCount()];
            """)).Elements;
        NearVector(values[0], 1.5, 0, 0); Assert.Equal(0, Number(values[1]));
        Assert.Equal(0, JoltLifetime.WorldCount);
    }

    [Fact]
    public void DesktopDisposalReleasesUndisposedWorldsAndRejectsRetainedModuleCalls() {
        var builtins = new DesktopBuiltins();
        var world = Assert.IsType<ModuleObj>(Evaluate(World + "w.CreateSphere(1,[0,0,0],1);w;", builtins));
        Assert.Equal(1, JoltLifetime.WorldCount);
        builtins.Dispose(); builtins.Dispose();
        Assert.Equal(0, JoltLifetime.WorldCount);
        var count = Assert.IsType<BuiltinObj>(world._Environment.Get("GetBodyCount", out _));
        Assert.Contains("disposed", Assert.IsType<ErrorObj>(count.Fn(System.Array.Empty<IObject>())).Message);
    }

    private static IObject Call(ModuleObj module, string name, params IObject[] args) =>
        Assert.IsType<BuiltinObj>(module._Environment.Get(name, out _)).Fn(args);
    private static ArrayObj ZeroVector() => new(new IObject[] { new IntegerObj(0), new IntegerObj(0), new IntegerObj(0) });

    [Fact]
    public void UnderflowingMassIsRejectedRatherThanTurningADynamicBodyStatic() {
        using var builtins = new DesktopBuiltins();
        var world = Assert.IsType<ModuleObj>(Evaluate(World + "w;", builtins));
        foreach (double mass in new[] { double.Epsilon, -double.Epsilon }) {
            var result = Call(world, "CreateSphere", new DoubleObj(1), ZeroVector(), new DoubleObj(mass));
            Assert.Contains("Mass", Assert.IsType<ErrorObj>(result).Message);
        }
        Assert.Equal(0, Number(Call(world, "GetBodyCount")));
    }

    [Fact]
    public void NativeFoundationRemainsAliveWhileAnotherDesktopRuntimeOwnsAWorld() {
        using var first = new DesktopBuiltins();
        using var second = new DesktopBuiltins();
        Evaluate(World + "w.CreateSphere(1,[0,0,0],1);", first);
        var values = Assert.IsType<ArrayObj>(Evaluate(World + "變數 b=w.CreateSphere(1,[0,0,0],1);[w,b];", second)).Elements;
        var world = Assert.IsType<ModuleObj>(values[0]);
        Assert.Equal(2, JoltLifetime.WorldCount);
        first.Dispose(); Assert.Equal(1, JoltLifetime.WorldCount);
        Call(world, "AddImpulse", values[1], new ArrayObj(new IObject[] { new IntegerObj(2), new IntegerObj(0), new IntegerObj(0) }));
        Assert.IsType<NullObj>(Call(world, "Step", new DoubleObj(0.25), new IntegerObj(1)));
        NearVector(Call(world, "GetPosition", values[1]), 0.5, 0, 0);
        second.Dispose(); Assert.Equal(0, JoltLifetime.WorldCount);
    }

    [Fact]
    public void ChangingGravityWakesSleepingBodies() {
        var values = Assert.IsType<ArrayObj>(Evaluate(World + """
            變數 b=w.CreateSphere(0.5d,[0,0,0],1);
            迴圈(變數 i=0;i<120;i++){w.Step(1.0d/60,1);}
            變數 asleep=w.IsActive(b);w.SetGravity([0,-10,0]);w.Step(0.1d,1);
            [asleep,w.IsActive(b),w.GetGravity(),w.GetLinearVelocity(b)];
            """)).Elements;
        Assert.False(Assert.IsType<BooleanObj>(values[0]).Value);
        Assert.True(Assert.IsType<BooleanObj>(values[1]).Value);
        NearVector(values[2], 0, -10, 0); NearVector(values[3], 0, -1, 0);
    }

    [Fact]
    public void BodyCapacityReturnsAnErrorAndRemovedSlotsCanBeReused() {
        using var builtins = new DesktopBuiltins();
        var values = Assert.IsType<ArrayObj>(Evaluate(World + """
            變數 first=w.CreateSphere(0.1d,[0,0,0],0);
            迴圈(變數 i=1;i<j.MaxBodies;i++){w.CreateSphere(0.1d,[0,0,0],0);};
            [w,first];
            """, builtins)).Elements;
        var world = Assert.IsType<ModuleObj>(values[0]);
        Assert.Equal(4096, Number(Call(world, "GetBodyCount")));
        Assert.Contains("capacity", Assert.IsType<ErrorObj>(Call(world, "CreateSphere", new DoubleObj(0.1), ZeroVector(), new IntegerObj(0))).Message);
        Call(world, "RemoveBody", values[1]);
        Assert.Equal(4095, Number(Call(world, "GetBodyCount")));
        Assert.IsType<JoltBodyObject>(Call(world, "CreateSphere", new DoubleObj(0.1), ZeroVector(), new IntegerObj(0)));
        Assert.Equal(4096, Number(Call(world, "GetBodyCount")));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DisposedNativeSystem() {
        lock (JoltLifetime.Gate) {
            using var world = new JoltWorld(Vector3.Zero);
            var system = typeof(JoltWorld).GetField("system", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(world)!;
            return new WeakReference(system);
        }
    }

    [Fact]
    public void DisposalFreesNativeListenersSoPhysicsSystemCanBeGarbageCollected() {
        var weak = DisposedNativeSystem();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(weak.IsAlive); Assert.Equal(0, JoltLifetime.WorldCount);
    }
}
