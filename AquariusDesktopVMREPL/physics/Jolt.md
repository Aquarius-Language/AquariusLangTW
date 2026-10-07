# Jolt Physics desktop module

Import `Jolt` (or its alias `JoltPhysics`) in the desktop VM, REPL or compiled
`.bottle`. Importing only registers the module; `CreateWorld` initializes native
physics. Jolt runs headlessly and requires no graphics bridge, GPU or window.

```aqua
變數 物理 = 匯入("Jolt");
變數 世界 = 物理.CreateWorld([0, -9.81d, 0]);
變數 地板 = 世界.CreateBox([10, 0.5d, 10], [0, -0.5d, 0], 0);
變數 球 = 世界.CreateSphere(0.5d, [0, 2, 0], 2);
世界.AddImpulse(球, [4, 0, 0]);
迴圈 (變數 i = 0; i < 60; i++) { 世界.Step(1.0d / 60, 1); }
印出(世界.GetPosition(球), 世界.GetLinearVelocity(球));
世界.Dispose();
```

Use meters, kilograms and seconds. Vectors are `[x,y,z]`; rotations are
quaternions `[x,y,z,w]`, normalized by `SetRotation`. Aquarius decimal literals
need an `f` or `d` suffix. The native simulation uses single precision.

## API

`Jolt.Backend` is `"Jolt Physics"`; `Jolt.MaxBodies` is 4096.
`Jolt.CreateWorld(gravity)` returns a world module with these methods:

| Method | Behavior |
| --- | --- |
| `CreateSphere(radius, position, mass)` | Create and add a sphere, returning an opaque body. |
| `CreateBox(halfExtents, position, mass)` | Create and add a box. Extents are **half** its full dimensions. |
| `Step(seconds, collisionSteps)` | Advance by the specified duration, subdivided into collision steps. |
| `GetGravity()` / `SetGravity(vector)` | Read/set acceleration in m/s²; setting gravity wakes dynamic bodies. |
| `GetPosition(body)` / `SetPosition(body, vector)` | Read/set the shape origin in meters. |
| `GetRotation(body)` / `SetRotation(body, quaternion)` | Read/set orientation. |
| `GetLinearVelocity(body)` / `SetLinearVelocity(body, vector)` | Read/set velocity in m/s. |
| `GetAngularVelocity(body)` / `SetAngularVelocity(body, vector)` | Read/set angular velocity in rad/s. |
| `AddForce(body, vector)` | Apply force in newtons at the center of mass for the next `Step`. |
| `AddImpulse(body, vector)` | Apply impulse in N·s immediately: velocity change = impulse / mass. |
| `AddAngularImpulse(body, vector)` | Apply angular impulse in kg·m²/s using the shape's inertia tensor. |
| `SetFriction(body, value)` | Set friction in 0..1 (initially 0.2). |
| `SetRestitution(body, value)` | Set restitution in 0..1 (initially 0). |
| `IsActive(body)` | Whether a body is awake in the simulation. |
| `GetBodyCount()` | Number of bodies in the world. |
| `RemoveBody(body)` | Remove and destroy a body; subsequent use is an error. |
| `OptimizeBroadPhase()` | Optimize after adding a batch of bodies; avoid calling every frame. |
| `Dispose()` | Release the world and all its bodies; repeat calls are safe. |

Mass 0 creates a static body. Positive mass creates a dynamic body and scales
the shape's calculated inertia to that mass. Linear/angular damping starts at
zero. Dynamic bodies collide with static and dynamic bodies; static pairs do
not collide. Dynamic bodies use Jolt's linear-cast continuous collision mode
and sleep when at rest. Velocity setters, forces and impulses require dynamic
bodies. Native velocity limits remain Jolt's defaults (500 m/s and 0.25π×60 rad/s).

`Step` accepts 0.000001..1 seconds and 1..128 integer collision steps. Prefer
fixed 1/60-second updates; for longer durations use at least
`ceil(seconds / (1/60))` collision steps. Forces clear after each `Step`, so
apply sustained forces before every update. Without contacts or damping, Jolt
uses semi-implicit Euler: update velocity from acceleration, then update position
from that velocity. Expect time-step error relative to continuous free fall.
The default contact solver permits about 0.02 m of penetration, so a radius-0.5 m
sphere may rest with its center near 0.48 m above the floor.

Radius and each box half extent must be in 0.001..10000 m. Dynamic mass must be
in 0.001..1000000 kg. Numeric components must be finite and within ±1000000.
Wrong argument counts/types, invalid values, exhausted capacity, stale bodies,
bodies from another world, calls after disposal and native update failures become
Aquarius errors. Bodies cannot be transferred between worlds. This API covers
rigid spheres and boxes; it does not currently expose constraints, meshes,
kinematic bodies or character/vehicle controllers.

The desktop runtime also releases worlds when a script/bottle finishes or on
REPL shutdown, including scripts ending with errors. Separate worlds share
reference-counted native initialization. Calls are serialized, with native jobs
executed on the calling thread, so each world uses no worker-thread pool.

## Dependencies and distribution

The desktop project pins [JoltPhysicsSharp 2.19.1](https://www.nuget.org/packages/JoltPhysicsSharp/2.19.1)
and `JoltPhysics.Native` 1.0.4. This is the last binding release targeting .NET 8;
later releases require newer .NET. NuGet provides `joltc` native runtime assets
for Windows x64/ARM64, Linux x64/ARM64 and macOS. Keep the native assets and
`licenses/` directory with build/publish output. No separate CMake build is
needed for Jolt. Linux assets depend on the platform's C++ runtime; use a host
compatible with the native package.

`JoltOwnership` contains a version-specific workaround for two binding constructors
that leave native ownership disabled in 2.19.1. It enables normal disposal of
`PhysicsSystem` and shape-based `BodyCreationSettings`, including the system's
native listener GCHandle. The native system owns its collision filters.
Recheck this workaround when upgrading the pinned package; a regression test
verifies that a disposed system becomes garbage collectible.

## Verification

All Jolt tests run by default, including the real native engine. No GPU opt-in
flags are needed. Numerical unit tests check constant velocity, free fall with
multiple collision steps, mass-dependent impulses, force integration/clearing,
sphere/box inertia, restitution and settled collisions. They also cover input
validation, ownership, body removal and world/runtime cleanup.

The CLI smoke tests launch separate processes to execute the numerical example
as source and as a bottle after deleting the source, and check output/exit codes.
CI runs these on Windows, Linux and macOS; local verification is platform-specific.

```powershell
dotnet test AquariusLangVMTesting -c Release --filter FullyQualifiedName~Jolt
dotnet run --project AquariusDesktopVMREPL -- examples/jolt_physics/main.aqua
```

Successful smoke output is `JOLT_SMOKE_OK` followed by `真`. A numerical check
failure deliberately references a descriptive undefined identifier, producing
a language error and exit code 1.
