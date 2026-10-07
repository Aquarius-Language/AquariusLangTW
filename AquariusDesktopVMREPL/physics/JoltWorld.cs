using System.Numerics;
using System.Runtime.CompilerServices;
using AquariusLang.Object;
using JoltPhysicsSharp;

namespace AquariusREPL.Physics;

internal static class JoltOwnership {
    // In the pinned 2.19.1 binding, PhysicsSystem and the Shape-based
    // BodyCreationSettings constructors omit OwnsHandle=true. Without this,
    // Dispose skips their native destructors (and the system's listener GCHandle).
    // Keep this narrow workaround tied to the pinned package version.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_OwnsHandle")]
    private static extern void SetOwnsHandle(NativeObject instance, bool value);

    internal static T Own<T>(T instance) where T : NativeObject {
        SetOwnsHandle(instance, true);
        return instance;
    }
}

// Jolt's factory and native physics-system registry are process-wide. Serialize
// calls from separate desktop runtimes and keep the factory alive until the last
// world has released all its native resources.
internal static class JoltLifetime {
    internal static readonly object Gate = new();
    internal static int WorldCount { get; private set; }

    internal static void Acquire() {
        lock (Gate) {
            if (WorldCount == 0 && !Foundation.Init(false))
                throw new InvalidOperationException("Jolt initialization failed.");
            WorldCount++;
        }
    }

    internal static void Release() {
        lock (Gate) {
            if (--WorldCount == 0) Foundation.Shutdown();
        }
    }
}

internal sealed class JoltBodyObject : IObject {
    internal readonly JoltWorld World;
    internal readonly BodyID ID;
    internal readonly bool Dynamic;
    internal bool Removed;
    internal JoltBodyObject(JoltWorld world, BodyID id, bool dynamic) {
        World = world; ID = id; Dynamic = dynamic;
    }
    public string Type() => "JOLT_BODY";
    public string Inspect() => Removed || World.Disposed ? "removed Jolt body" : "Jolt body";
}

internal sealed class JoltWorld : IDisposable {
    internal const int MaxBodies = 4096;
    private readonly HashSet<JoltBodyObject> bodies = new();
    private readonly ObjectLayerPairFilterTable pairFilter;
    private readonly BroadPhaseLayerInterfaceTable broadPhase;
    private readonly ObjectVsBroadPhaseLayerFilterTable broadPhaseFilter;
    private readonly PhysicsSystem system;
    private readonly JobSystemThreadPool jobs;
    internal bool Disposed { get; private set; }
    internal int BodyCount => bodies.Count;
    internal BodyInterface Interface => system.BodyInterface;

    internal JoltWorld(Vector3 gravity) {
        JoltLifetime.Acquire();
        try {
            pairFilter = new(2);
            pairFilter.EnableCollision(0, 1);
            pairFilter.EnableCollision(1, 1);
            broadPhase = new(2, 2);
            broadPhase.MapObjectToBroadPhaseLayer(0, 0);
            broadPhase.MapObjectToBroadPhaseLayer(1, 1);
            broadPhaseFilter = new(broadPhase, 2, pairFilter, 2);
            system = JoltOwnership.Own(new PhysicsSystem(new PhysicsSystemSettings {
                MaxBodies = MaxBodies, MaxBodyPairs = 65536, MaxContactConstraints = 16384,
                ObjectLayerPairFilter = pairFilter, BroadPhaseLayerInterface = broadPhase,
                ObjectVsBroadPhaseLayerFilter = broadPhaseFilter
            }));
            // Zero worker threads runs jobs on the calling thread. This avoids
            // creating a machine-sized thread pool for each script world.
            jobs = new JobSystemThreadPool(new JobSystemThreadPoolConfig {
                maxJobs = Foundation.MaxPhysicsJobs, maxBarriers = Foundation.MaxPhysicsBarriers, numThreads = 0
            });
            system.Gravity = gravity;
        } catch {
            jobs?.Dispose();
            system?.Dispose();
            broadPhaseFilter?.Dispose(); broadPhase?.Dispose(); pairFilter?.Dispose();
            JoltLifetime.Release();
            throw;
        }
    }

    internal void RequireLive() {
        if (Disposed) throw new InvalidOperationException("Jolt world has been disposed.");
    }

    internal JoltBodyObject Body(IObject value, bool dynamic = false) {
        RequireLive();
        if (value is not JoltBodyObject body || body.World != this || body.Removed || !bodies.Contains(body))
            throw new ArgumentException("Expected a live body from this Jolt world.");
        if (dynamic && !body.Dynamic) throw new ArgumentException("Expected a dynamic body.");
        return body;
    }

    internal JoltBodyObject Create(Shape shape, Vector3 position, float mass) {
        RequireLive();
        if (bodies.Count >= MaxBodies) throw new InvalidOperationException($"Jolt world capacity is {MaxBodies} bodies.");
        bool dynamic = mass > 0;
        using var settings = JoltOwnership.Own(new BodyCreationSettings(shape, position, Quaternion.Identity,
            dynamic ? MotionType.Dynamic : MotionType.Static, (ObjectLayer)(ushort)(dynamic ? 1 : 0)) {
            LinearDamping = 0, AngularDamping = 0, Friction = 0.2f, Restitution = 0,
            MotionQuality = dynamic ? MotionQuality.LinearCast : MotionQuality.Discrete
        });
        if (dynamic) {
            settings.OverrideMassProperties = OverrideMassProperties.CalculateInertia;
            settings.MassPropertiesOverride = new MassProperties { Mass = mass };
        }
        BodyID id = Interface.CreateAndAddBody(settings, dynamic ? Activation.Activate : Activation.DontActivate);
        if (id.IsInvalid) throw new InvalidOperationException("Jolt could not allocate a body.");
        var body = new JoltBodyObject(this, id, dynamic);
        bodies.Add(body);
        return body;
    }

    internal Vector3 Gravity {
        get => system.Gravity;
        set {
            system.Gravity = value;
            foreach (var body in bodies.Where(b => b.Dynamic)) Interface.ActivateBody(body.ID);
        }
    }

    internal void Step(float seconds, int collisionSteps) {
        PhysicsUpdateError error = system.Update(seconds, collisionSteps, jobs);
        if (error != PhysicsUpdateError.None)
            throw new InvalidOperationException($"Jolt update failed: {error}.");
    }

    internal void OptimizeBroadPhase() => system.OptimizeBroadPhase();

    internal void Remove(JoltBodyObject body) {
        Interface.RemoveAndDestroyBody(body.ID);
        body.Removed = true;
        bodies.Remove(body);
    }

    public void Dispose() {
        lock (JoltLifetime.Gate) {
            if (Disposed) return;
            foreach (var body in bodies.ToArray()) Remove(body);
            jobs.Dispose();
            system.Dispose();
            // joltc's PhysicsSystem owns and deletes the native filters. These
            // wrappers have no native destructor; dispose them to unregister handles.
            broadPhaseFilter.Dispose(); broadPhase.Dispose(); pairFilter.Dispose();
            Disposed = true;
            JoltLifetime.Release();
        }
    }
}
