using System.Numerics;
using AquariusLang.Object;
using AquariusLang.Physics;
using JoltPhysicsSharp;

namespace AquariusREPL.Physics;

internal sealed class NativeJoltBackend : IPhysicsBackend {
    public string Name => "Jolt Physics";
    public IPhysicsWorld CreateWorld(Vector3 gravity) => new NativeWorld(gravity);
    private sealed class NativeWorld : IPhysicsWorld {
        private readonly JoltWorld world;
        internal NativeWorld(Vector3 gravity) { lock (JoltLifetime.Gate) world = new(gravity); }
        private T Locked<T>(Func<T> action) { lock (JoltLifetime.Gate) { world.RequireLive(); return action(); } }
        private void Locked(Action action) => Locked(() => { action(); return true; });
        public void RequireLive() => Locked(() => true);
        public int BodyCount => Locked(() => world.BodyCount);
        public Vector3 Gravity { get => Locked(() => world.Gravity); set => Locked(() => world.Gravity = value); }
        public IObject CreateSphere(float radius, Vector3 position, float mass) => Locked(() => { using var shape = new SphereShape(radius); return world.Create(shape, position, mass); });
        public IObject CreateBox(Vector3 half, Vector3 position, float mass) => Locked(() => {
            using var shape = new BoxShape(half, MathF.Min(Foundation.DefaultConvexRadius, MathF.Min(half.X, MathF.Min(half.Y, half.Z)) * .1f));
            return world.Create(shape, position, mass);
        });
        public void Step(float seconds, int steps) => Locked(() => world.Step(seconds, steps));
        public void Remove(IObject body) => Locked(() => world.Remove(world.Body(body)));
        public Vector3 GetPosition(IObject body) => Locked(() => world.Interface.GetPosition(world.Body(body).ID));
        public void SetPosition(IObject body, Vector3 v) => Locked(() => world.Interface.SetPosition(world.Body(body).ID, v, Activation.Activate));
        public Quaternion GetRotation(IObject body) => Locked(() => world.Interface.GetRotation(world.Body(body).ID));
        public void SetRotation(IObject body, Quaternion v) => Locked(() => world.Interface.SetRotation(world.Body(body).ID, v, Activation.Activate));
        public Vector3 GetLinearVelocity(IObject body) => Locked(() => world.Interface.GetLinearVelocity(world.Body(body).ID));
        public void SetLinearVelocity(IObject body, Vector3 v) => Locked(() => world.Interface.SetLinearVelocity(world.Body(body, true).ID, v));
        public Vector3 GetAngularVelocity(IObject body) => Locked(() => world.Interface.GetAngularVelocity(world.Body(body).ID));
        public void SetAngularVelocity(IObject body, Vector3 v) => Locked(() => { var id = world.Body(body, true).ID; world.Interface.SetAngularVelocity(id, v); world.Interface.ActivateBody(id); });
        public void AddForce(IObject body, Vector3 v) => Locked(() => { var id = world.Body(body, true).ID; world.Interface.ActivateBody(id); world.Interface.AddForce(id, v); });
        public void AddImpulse(IObject body, Vector3 v) => Locked(() => world.Interface.AddImpulse(world.Body(body, true).ID, v));
        public void AddAngularImpulse(IObject body, Vector3 v) => Locked(() => world.Interface.AddAngularImpulse(world.Body(body, true).ID, v));
        public void SetFriction(IObject body, float v) => Locked(() => world.Interface.SetFriction(world.Body(body).ID, v));
        public void SetRestitution(IObject body, float v) => Locked(() => world.Interface.SetRestitution(world.Body(body).ID, v));
        public bool IsActive(IObject body) => Locked(() => world.Interface.IsActive(world.Body(body).ID));
        public void OptimizeBroadPhase() => Locked(world.OptimizeBroadPhase);
        public void Dispose() { lock (JoltLifetime.Gate) world.Dispose(); }
    }
}
