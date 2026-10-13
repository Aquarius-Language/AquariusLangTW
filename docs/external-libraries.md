# External libraries and platform capabilities

## Requirement for future libraries

Every future included computational library, its Aquarius binding and its transitive
computational dependencies must execute in Wasm. Use an approved core-Wasm ABI or
a supported Wasm component interface, with explicit imports/exports, versions,
memory ownership, capabilities and desktop/browser validation. A `.wasm` file
that delegates its algorithms to C# or JavaScript does not meet this requirement.

Host adapters provide necessary OS, browser and GPU services. Browser JavaScript
may load modules, transport buffers/handles and deliver asynchronous events; it
must not implement portable library algorithms. Retain C# only for a documented
native integration that still needs it. A library without a compatible Wasm build
must be ported/rebuilt or deferred, rather than replaced with a managed/native
algorithm provider or subprocess.

The profiles and Jolt implementation below describe the current transitional
architecture. They do not authorize new Emscripten/JS algorithm implementations.
See the [complete migration checklist](wasm-architecture-migration.md) for ABI,
linking, packaging and existing-library migration work.

## Current implementation

ONNX Runtime is the explicitly documented native inference integration described in
[the ONNX guide](onnx.md): desktop uses the official native WebGPU/Dawn plugin,
browser uses ORT Wasm/WebGPU, and core owns contract version 1 and browser assets.
It uses `HostCapability` and is a narrow exception to the future-library Wasm rule.

MediaPipe pose preprocessing and postprocessing use `CoreWasm` ABI 1 and the
same isolated `pose.wasm` on desktop and browser. Its adapters compose two ONNX
sessions and bulk image/tensor transfers; pose math is never implemented in host
code. See the pose pipeline section of the ONNX guide.

Core owns library identities, pinned versions, contract versions, portable assets,
argument validation, language registration, aliases and object ownership rules.
Platform projects provide adapters and execution engines. Core has no Wasmtime,
ClearScript, Silk.NET or native-library package dependencies.

`ExternalLibraryDescriptor` distinguishes three profiles:

| Profile | Deployment and execution |
| --- | --- |
| `CoreWasm` | Memory-only libraries with an explicit Wasm ABI. Audio and MediaPipe pose implement ABI 1; the general library loader remains future work. The CLR factory registry rejects this profile. |
| `Emscripten` | A Wasm module plus generated JavaScript glue; requires an Emscripten-capable host. |
| `HostCapability` | A platform API such as WebGPU, windowing or user documents; represented by a core contract. |

`ExternalLibraryRegistry` belongs to a host session. Registration checks the
contract type and version, rejects duplicate adapters, creates each adapter lazily,
and disposes owned instances in reverse creation order. Resolving an unavailable
capability fails explicitly. Engine choice never silently falls back to another
library implementation.

Audio uses pinned miniaudio 0.11.25 in `AquariusCore/audio/audio.wasm`, with no
host imports and explicit allocation, sound ownership, control and PCM rendering
exports. Core embeds the Wasm, JS ABI adapter and license. Desktop executes it
in Wasmtime and sends PCM to a miniaudio device ring; browser playback executes
the same Wasm in an AudioWorklet. Neither host implements decoder or mixer
algorithms. The library ABI is documented in [the audio guide](../AquariusCore/audio/README.md).

Computational libraries cannot be registered as CLR factories with a `CoreWasm`
label. New Emscripten adapters are rejected; the existing pinned Jolt descriptor
is the transitional exception. This admission gate does not replace the pending
library manifest, linker, feature validation and Wasm binding implementation.

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
glue; it is not an Aquarius execution engine. Desktop Jolt uses one lazily created,
process-lifetime V8 runtime on its dedicated worker thread to avoid isolate teardown
crashes during concurrent world lifecycles and CLR garbage collection. Each world
owns a separate script context and Emscripten instance with independent Wasm memory.
Disposal releases its physics objects and script handles; the shared V8 runtime
remains available until process exit. Both platforms use 4,096 bodies, 8,192 body
pairs and 4,096 contact constraints, including the same validation and ownership
behavior.

WebGPU retains `IWgpuBackend` and device/resource contracts in core, with shared
WGSL shaders, layouts and the 1,120-byte Processing uniform ABI. Desktop implements
the contract with Silk.NET/wgpu-native; browsers implement it with WebGPU. Future
output platforms should implement this contract for their primary graphics layer.

For a new library, first satisfy the Wasm requirement above and define a versioned,
language-neutral ABI. Declare its Wasm/data/license assets and deployment names,
dependencies, required features, capability imports, ownership and error transport.
Add cross-platform behavior, validation, linking and disposal tests. The existing
CLR-interface registry and Emscripten hosting are transitional mechanisms; the
checklist identifies the changes needed for general Wasm library inclusion.
Do not infer compatibility from the `.wasm` extension alone.

Upstream: [JoltPhysics.js](https://github.com/jrouwe/JoltPhysics.js),
[Wasmtime .NET](https://github.com/bytecodealliance/wasmtime-dotnet),
[ClearScript](https://github.com/ClearFoundry/ClearScript).
