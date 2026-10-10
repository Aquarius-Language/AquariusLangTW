# External libraries and platform capabilities

Core owns library identities, pinned versions, contract versions, portable assets,
argument validation, language registration, aliases and object ownership rules.
Platform projects provide adapters and execution engines. Core has no Wasmtime,
ClearScript, Silk.NET or native-library package dependencies.

`ExternalLibraryDescriptor` distinguishes three profiles:

| Profile | Deployment and execution |
| --- | --- |
| `CoreWasm` | A freestanding module with explicit Wasm imports; a host may link it with Wasmtime or browser WebAssembly. |
| `Emscripten` | A Wasm module plus generated JavaScript glue; requires an Emscripten-capable host. |
| `HostCapability` | A platform API such as WebGPU, windowing or user documents; represented by a core contract. |

`ExternalLibraryRegistry` belongs to a host session. Registration checks the
contract type and version, rejects duplicate adapters, creates each adapter lazily,
and disposes owned instances in reverse creation order. Resolving an unavailable
capability fails explicitly. Engine choice never silently falls back to another
library implementation.

Jolt uses the pinned `jolt-physics` 0.24.0 Emscripten build. `AquariusCore/external/jolt`
contains the shared world implementation and generated vendor assets; the npm
preparation script copies the pinned package's glue, binary and license there.
Core embeds those assets. The web exporter enumerates every catalogued library, validates deployment-name collisions, reads its assets from core and emits asset
SHA-256 hashes with the external-library declarations. Desktop reads the same
embedded assets. There is no desktop JoltPhysicsSharp or joltc dependency.

The browser adapter supplies the loader and animation environment. Desktop hosts
the trusted Emscripten glue in ClearScript/V8, instantiates its Wasm synchronously,
and adapts the shared world methods to `IPhysicsWorld`. Aquarius application code
executes in Wasmtime. V8 is isolated to external libraries that need JavaScript
glue; it is not an Aquarius execution engine. Each desktop physics world owns its
Emscripten instance and releases it on disposal. Both platforms use 4,096 bodies,
8,192 body pairs and 4,096 contact constraints, including the same validation and
ownership behavior.

WebGPU retains `IWgpuBackend` and device/resource contracts in core, with shared
WGSL shaders, layouts and the 1,120-byte Processing uniform ABI. Desktop implements
the contract with Silk.NET/wgpu-native; browsers implement it with WebGPU. Future
output platforms should implement this contract for their primary graphics layer.

For a new library, define a platform-neutral contract and versioned descriptor,
choose its actual execution profile, declare every binary/glue/license asset and its deployment name,
implement adapters in the host projects, register them in the composition roots,
and add cross-platform behavior, validation, ownership and disposal tests. A raw
Wasm extension should specify memory ownership, export signatures, imports and
error transport; an Emscripten extension must include its glue and engine needs.
Do not infer compatibility from the `.wasm` extension alone.

Upstream: [JoltPhysics.js](https://github.com/jrouwe/JoltPhysics.js),
[Wasmtime .NET](https://github.com/bytecodealliance/wasmtime-dotnet),
[ClearScript](https://github.com/ClearFoundry/ClearScript).
