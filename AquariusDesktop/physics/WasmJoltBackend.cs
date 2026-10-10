using System.Numerics;
using System.Text;
using System.Threading;
using AquariusLang.External;
using AquariusLang.Object;
using AquariusLang.Physics;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

namespace AquariusLang.Desktop.Physics;

internal static class JoltLifetime
{
    private static int worlds;
    internal static int WorldCount => Volatile.Read(ref worlds);
    internal static void Acquire() => Interlocked.Increment(ref worlds);
    internal static void Release() => Interlocked.Decrement(ref worlds);
}

internal sealed class JoltBodyObject(WasmJoltWorld world, ScriptObject value) : IObject
{
    internal WasmJoltWorld World { get; } = world;
    internal ScriptObject Value { get; } = value;
    public string Type() => "JOLT_BODY";
    public string Inspect() => "Jolt body";
}

/// <summary>Emscripten host adapter. Aquarius itself runs in Wasmtime; Jolt uses the same core Wasm and glue as browsers.</summary>
internal sealed class WasmJoltBackend : IPhysicsBackend
{
    public string Name => "Jolt Physics";
    public IPhysicsWorld CreateWorld(Vector3 gravity) => new WasmJoltWorld(gravity);
}

internal sealed class WasmJoltWorld : IPhysicsWorld
{
    private readonly object gate = new();
    private readonly V8ScriptEngine engine;
    private readonly ScriptObject world;
    private bool disposed;
    internal WasmJoltWorld(Vector3 gravity)
    {
        engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion | V8ScriptEngineFlags.EnableArrayConversion);
        try
        {
            engine.Execute("globalThis.console={log(){},error(){},warn(){}};globalThis.performance={now:()=>Date.now()};");
            engine.AddHostObject("resolveLibraryUrl", new Func<string, string, string>((path, root) => new Uri(new Uri(root), path).AbsoluteUri));
            engine.Execute("globalThis.URL=class {constructor(path,root='https://aquarius.invalid/'){this.href=resolveLibraryUrl(path,root);}toString(){return this.href;}};");
            var info = new DocumentInfo(new Uri("https://aquarius.invalid/jolt.mjs")) { Category = ModuleCategory.Standard };
            engine.Execute(info, Encoding.UTF8.GetString(ExternalLibraries.ReadAsset(ExternalLibraries.Jolt, "vendor-jolt.mjs")) + "\nglobalThis.aquaInitJolt=Jolt;");
            var bytes = ExternalLibraries.ReadAsset(ExternalLibraries.Jolt, "vendor-jolt.wasm");
            using var array = (ITypedArray<byte>)engine.Evaluate($"globalThis.aquaJoltBytes=new Uint8Array({bytes.Length})");
            array.Write(bytes, 0, (ulong)bytes.Length, 0);
            var ready = (Task<object>)engine.Evaluate("aquaInitJolt({instantiateWasm(imports,receive){const instance=new WebAssembly.Instance(new WebAssembly.Module(aquaJoltBytes),imports);receive(instance);return instance.exports;}})");
            var jolt = ready.GetAwaiter().GetResult();
            engine.Script.aquaJolt = jolt;
            engine.Execute(new DocumentInfo("physics-core") { Category = ModuleCategory.Standard }, Encoding.UTF8.GetString(ExternalLibraries.ReadAsset(ExternalLibraries.Jolt, "physics.mjs")) + "\nglobalThis.aquaCreateJoltWorld=(g)=>new JoltWorld(aquaJolt,g);");
            world = (ScriptObject)engine.Script.aquaCreateJoltWorld(V(gravity));
            JoltLifetime.Acquire();
        }
        catch { engine.Dispose(); throw; }
    }
    private static float[] V(Vector3 v) => new[] { v.X, v.Y, v.Z };
    private T Locked<T>(Func<T> fn) { lock (gate) { RequireLive(); try { return fn(); } catch (ScriptEngineException e) { throw new InvalidOperationException(e.Message, e); } } }
    private void Action(Action fn) => Locked(() => { fn(); return true; });
    private ScriptObject Body(IObject body)
    {
        if (body is not JoltBodyObject b || b.World != this) throw new ArgumentException("Expected a live body from this Jolt world.");
        return b.Value;
    }
    private object Get(string name, IObject body) => world.InvokeMethod("get", name, Body(body));
    private void Set(string name, IObject body, object value) => Action(() => world.InvokeMethod("set", name, Body(body), value));
    private static Vector3 Vector(object value) { var a = (object[])value; return new(Convert.ToSingle(a[0]), Convert.ToSingle(a[1]), Convert.ToSingle(a[2])); }
    public void RequireLive() { if (disposed) throw new InvalidOperationException("Jolt world has been disposed."); }
    public int BodyCount => Locked(() => Convert.ToInt32(((ScriptObject)world.GetProperty("bodies")).GetProperty("size")));
    public Vector3 Gravity { get => Locked(() => Vector(world.InvokeMethod("getGravity"))); set => Action(() => world.InvokeMethod("setGravity", V(value))); }
    public IObject CreateSphere(float radius, Vector3 position, float mass) => Locked(() => new JoltBodyObject(this, (ScriptObject)world.InvokeMethod("create", "sphere", radius, V(position), mass)));
    public IObject CreateBox(Vector3 half, Vector3 position, float mass) => Locked(() => new JoltBodyObject(this, (ScriptObject)world.InvokeMethod("create", "box", V(half), V(position), mass)));
    public void Step(float seconds, int collisionSteps) => Action(() => world.InvokeMethod("step", seconds, collisionSteps));
    public void Remove(IObject body) => Action(() => world.InvokeMethod("remove", Body(body)));
    public Vector3 GetPosition(IObject body) => Locked(() => Vector(Get("GetPosition", body)));
    public void SetPosition(IObject body, Vector3 v) => Set("SetPosition", body, V(v));
    public Quaternion GetRotation(IObject body) => Locked(() => { var a = (object[])Get("GetRotation", body); return new Quaternion(Convert.ToSingle(a[0]), Convert.ToSingle(a[1]), Convert.ToSingle(a[2]), Convert.ToSingle(a[3])); });
    public void SetRotation(IObject body, Quaternion v) => Set("SetRotation", body, new[] { v.X, v.Y, v.Z, v.W });
    public Vector3 GetLinearVelocity(IObject body) => Locked(() => Vector(Get("GetLinearVelocity", body)));
    public void SetLinearVelocity(IObject body, Vector3 v) => Set("SetLinearVelocity", body, V(v));
    public Vector3 GetAngularVelocity(IObject body) => Locked(() => Vector(Get("GetAngularVelocity", body)));
    public void SetAngularVelocity(IObject body, Vector3 v) => Set("SetAngularVelocity", body, V(v));
    public void AddForce(IObject body, Vector3 v) => Set("AddForce", body, V(v));
    public void AddImpulse(IObject body, Vector3 v) => Set("AddImpulse", body, V(v));
    public void AddAngularImpulse(IObject body, Vector3 v) => Set("AddAngularImpulse", body, V(v));
    public void SetFriction(IObject body, float v) => Set("SetFriction", body, v);
    public void SetRestitution(IObject body, float v) => Set("SetRestitution", body, v);
    public bool IsActive(IObject body) => Locked(() => Convert.ToBoolean(Get("IsActive", body)));
    public void OptimizeBroadPhase() => Action(() => ((ScriptObject)world.GetProperty("system")).InvokeMethod("OptimizeBroadPhase"));
    public void Dispose() { lock (gate) { if (disposed) return; try { world.InvokeMethod("dispose"); } finally { disposed = true; engine.Dispose(); JoltLifetime.Release(); } } }
}
