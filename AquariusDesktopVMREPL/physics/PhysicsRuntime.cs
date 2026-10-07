using System.Numerics;
using AquariusLang.Object;
using AquariusLang.runtime;
using JoltPhysicsSharp;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusREPL.Physics;

internal sealed class PhysicsRuntime : IDisposable {
    private readonly ModuleObj module;
    private readonly HashSet<JoltWorld> worlds = new();
    private bool disposed;

    internal PhysicsRuntime() {
        var env = AquaEnvironment.NewEnvironment();
        module = new ModuleObj(env);
        env.Create("Backend", new StringObj("Jolt Physics"));
        env.Create("MaxBodies", new IntegerObj(JoltWorld.MaxBodies));
        Bind(env, "CreateWorld", 1, a => CreateWorld(Vector(a[0])));
    }

    internal bool TryImport(string name, out ModuleObj result) {
        result = module;
        return name is "Jolt" or "JoltPhysics";
    }

    private void Bind(AquaEnvironment env, string name, int count, Func<IObject[], IObject> fn) {
        env.Create(name, new BuiltinObj(args => {
            lock (JoltLifetime.Gate) {
                try {
                    if (disposed) throw new InvalidOperationException("Jolt runtime has been disposed.");
                    if (args.Length != count) throw new ArgumentException($"Expected {count} arguments, got {args.Length}.");
                    return fn(args);
                } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DllNotFoundException
                    or EntryPointNotFoundException or BadImageFormatException or OverflowException or TypeInitializationException) {
                    return new ErrorObj($"Jolt.{name}: {ex.GetBaseException().Message}");
                }
            }
        }));
    }

    private ModuleObj CreateWorld(Vector3 gravity) {
        var world = new JoltWorld(gravity);
        worlds.Add(world);
        var env = AquaEnvironment.NewEnvironment();
        void Action(string name, int count, Func<IObject[], IObject> fn) =>
            Bind(env, name, count, a => { world.RequireLive(); return fn(a); });
        Bind(env, "Dispose", 0, _ => { world.Dispose(); worlds.Remove(world); return Null(); });
        Action("GetBodyCount", 0, _ => new IntegerObj(world.BodyCount));
        Action("GetGravity", 0, _ => Vector(world.Gravity));
        Action("SetGravity", 1, a => { world.Gravity = Vector(a[0]); return Null(); });
        Action("Step", 2, a => {
            float dt = Scalar(a[0]);
            if (dt < 0.000001f || dt > 1) throw new ArgumentException("Step seconds must be in 0.000001..1.");
            int steps = Integer(a[1]);
            if (steps < 1 || steps > 128) throw new ArgumentException("Collision steps must be in 1..128.");
            world.Step(dt, steps); return Null();
        });
        Action("CreateSphere", 3, a => {
            float radius = Dimension(a[0]); var position = Vector(a[1]); float mass = Mass(a[2]);
            using var shape = new SphereShape(radius);
            return world.Create(shape, position, mass);
        });
        Action("CreateBox", 3, a => {
            var half = Vector(a[0]);
            if (half.X < 0.001f || half.Y < 0.001f || half.Z < 0.001f || half.X > 10000 || half.Y > 10000 || half.Z > 10000)
                throw new ArgumentException("Box half extents must each be in 0.001..10000 meters.");
            var position = Vector(a[1]); float mass = Mass(a[2]);
            float convexRadius = MathF.Min(Foundation.DefaultConvexRadius, MathF.Min(half.X, MathF.Min(half.Y, half.Z)) * 0.1f);
            using var shape = new BoxShape(half, convexRadius);
            return world.Create(shape, position, mass);
        });
        Action("RemoveBody", 1, a => { world.Remove(world.Body(a[0])); return Null(); });
        Action("GetPosition", 1, a => Vector(world.Interface.GetPosition(world.Body(a[0]).ID)));
        Action("SetPosition", 2, a => {
            var body = world.Body(a[0]); var position = Vector(a[1]);
            world.Interface.SetPosition(body.ID, position, Activation.Activate); return Null();
        });
        Action("GetRotation", 1, a => {
            var q = world.Interface.GetRotation(world.Body(a[0]).ID);
            return Numbers(q.X, q.Y, q.Z, q.W);
        });
        Action("SetRotation", 2, a => {
            var body = world.Body(a[0]); var q = Rotation(a[1]);
            world.Interface.SetRotation(body.ID, q, Activation.Activate); return Null();
        });
        Action("GetLinearVelocity", 1, a => Vector(world.Interface.GetLinearVelocity(world.Body(a[0]).ID)));
        Action("SetLinearVelocity", 2, a => {
            var body = world.Body(a[0], true); var velocity = Vector(a[1]);
            world.Interface.SetLinearVelocity(body.ID, velocity); return Null();
        });
        Action("GetAngularVelocity", 1, a => Vector(world.Interface.GetAngularVelocity(world.Body(a[0]).ID)));
        Action("SetAngularVelocity", 2, a => {
            var body = world.Body(a[0], true); var velocity = Vector(a[1]);
            world.Interface.SetAngularVelocity(body.ID, velocity); world.Interface.ActivateBody(body.ID); return Null();
        });
        Action("AddForce", 2, a => {
            var body = world.Body(a[0], true); var force = Vector(a[1]);
            world.Interface.ActivateBody(body.ID); world.Interface.AddForce(body.ID, force); return Null();
        });
        Action("AddImpulse", 2, a => {
            var body = world.Body(a[0], true); var impulse = Vector(a[1]);
            world.Interface.AddImpulse(body.ID, impulse); return Null();
        });
        Action("AddAngularImpulse", 2, a => {
            var body = world.Body(a[0], true); var impulse = Vector(a[1]);
            world.Interface.AddAngularImpulse(body.ID, impulse); return Null();
        });
        Action("SetFriction", 2, a => {
            var body = world.Body(a[0]); float friction = UnitInterval(a[1]);
            world.Interface.SetFriction(body.ID, friction); return Null();
        });
        Action("SetRestitution", 2, a => {
            var body = world.Body(a[0]); float restitution = UnitInterval(a[1]);
            world.Interface.SetRestitution(body.ID, restitution); return Null();
        });
        Action("IsActive", 1, a => new BooleanObj(world.Interface.IsActive(world.Body(a[0]).ID)));
        Action("OptimizeBroadPhase", 0, _ => { world.OptimizeBroadPhase(); return Null(); });
        return new ModuleObj(env);
    }

    private static double Number(IObject value) {
        if (value is INumberObj number) {
            double n = number.GetNumValue();
            if (double.IsFinite(n) && Math.Abs(n) <= 1000000) return n;
        }
        throw new ArgumentException("Expected a finite number in -1000000..1000000.");
    }
    private static float Scalar(IObject value) => (float)Number(value);
    private static int Integer(IObject value) {
        if (value is not INumberObj number) throw new ArgumentException("Expected an integer.");
        double n = number.GetNumValue();
        if (!double.IsFinite(n) || n != Math.Truncate(n)) throw new ArgumentException("Expected an integer.");
        return checked((int)n);
    }
    private static float Dimension(IObject value) {
        float n = Scalar(value);
        if (n < 0.001f || n > 10000) throw new ArgumentException("Sphere radius must be in 0.001..10000 meters.");
        return n;
    }
    private static float Mass(IObject value) {
        double n = Number(value);
        // Validate before narrowing: an underflowing positive mass must not
        // silently become zero and create a static body.
        if (n != 0 && n < 0.001) throw new ArgumentException("Mass must be 0 (static) or in 0.001..1000000 kilograms.");
        return (float)n;
    }
    private static float UnitInterval(IObject value) {
        double n = Number(value);
        if (n < 0 || n > 1) throw new ArgumentException("Expected a value in 0..1.");
        return (float)n;
    }
    private static Vector3 Vector(IObject value) {
        if (value is not ArrayObj a || a.Elements.Length != 3) throw new ArgumentException("Expected a 3-element vector.");
        return new(Scalar(a.Elements[0]), Scalar(a.Elements[1]), Scalar(a.Elements[2]));
    }
    private static Quaternion Rotation(IObject value) {
        if (value is not ArrayObj a || a.Elements.Length != 4) throw new ArgumentException("Expected a 4-element quaternion [x,y,z,w].");
        var q = new Quaternion(Scalar(a.Elements[0]), Scalar(a.Elements[1]), Scalar(a.Elements[2]), Scalar(a.Elements[3]));
        if (q.LengthSquared() < 1e-12f) throw new ArgumentException("Quaternion must be nonzero.");
        return Quaternion.Normalize(q);
    }
    private static IObject Vector(Vector3 value) => Numbers(value.X, value.Y, value.Z);
    private static ArrayObj Numbers(params double[] values) => new(values.Select(x => (IObject)new DoubleObj(x)).ToArray());
    private static IObject Null() => RepeatedPrimitives.NULL;

    public void Dispose() {
        lock (JoltLifetime.Gate) {
            if (disposed) return;
            foreach (var world in worlds) world.Dispose();
            worlds.Clear(); disposed = true;
        }
    }
}
