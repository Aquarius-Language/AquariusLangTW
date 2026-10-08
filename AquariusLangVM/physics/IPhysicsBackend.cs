using System;
using System.Numerics;
using AquariusLang.Object;

namespace AquariusLang.Physics;

public interface IPhysicsBackend {
    string Name { get; }
    IPhysicsWorld CreateWorld(Vector3 gravity);
}
public interface IPhysicsWorld : IDisposable {
    int BodyCount { get; }
    Vector3 Gravity { get; set; }
    void RequireLive();
    IObject CreateSphere(float radius, Vector3 position, float mass);
    IObject CreateBox(Vector3 halfExtents, Vector3 position, float mass);
    void Step(float seconds, int collisionSteps);
    void Remove(IObject body);
    Vector3 GetPosition(IObject body);
    void SetPosition(IObject body, Vector3 value);
    Quaternion GetRotation(IObject body);
    void SetRotation(IObject body, Quaternion value);
    Vector3 GetLinearVelocity(IObject body);
    void SetLinearVelocity(IObject body, Vector3 value);
    Vector3 GetAngularVelocity(IObject body);
    void SetAngularVelocity(IObject body, Vector3 value);
    void AddForce(IObject body, Vector3 value);
    void AddImpulse(IObject body, Vector3 value);
    void AddAngularImpulse(IObject body, Vector3 value);
    void SetFriction(IObject body, float value);
    void SetRestitution(IObject body, float value);
    bool IsActive(IObject body);
    void OptimizeBroadPhase();
}
