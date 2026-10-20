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

These requirements govern compiler-bundled computational libraries. Explicitly
installed third-party Bottle packages may select native or Wasm implementations
through DynamicLibrary, according to application requirements. They do not enter
the bundled registry.
See the [complete migration checklist](wasm-architecture-migration.md) for ABI,
linking, packaging and existing-library migration work.

## Current implementation

ONNX Runtime is the explicitly documented native inference integration described in
[the ONNX guide](onnx.md): core owns contract version 2 (provider catalog, device
selection policy, tensor contract) and browser assets; desktop executes native ORT
providers (CUDA, TensorRT, Core ML, the WebGPU/Dawn plugin and optional OpenVINO/QNN
plugins), Android uses NNAPI/WebGPU/XNNPACK and browsers use ORT WebGPU/WebNN/Wasm.
It uses `HostCapability` and is a narrow exception to the future-library Wasm rule.

MediaPipe pose preprocessing and postprocessing use `CoreWasm` ABI 1 and the
same isolated `pose.wasm` on desktop and browser. Its adapters compose two ONNX
sessions and bulk image/tensor transfers; pose math is never implemented in host
code. See the pose pipeline section of the ONNX guide.

Core owns library identities, pinned versions, contract versions, portable assets,
argument validation, language registration, aliases and object ownership rules.
Platform projects provide adapters and execution engines. Core has no Wasmtime,
ClearScript, Silk.NET or native-library package dependencies.

[DynamicLibrary / 動態函式庫](dynamic-linking.md) is an explicit OS/browser
loading capability for application-supplied libraries. Its versioned buffer ABI
supports native DLL/SO wrappers, standalone ES module factories and import-free
Wasm modules through one bilingual API. It does not admit arbitrary computational
libraries into the bundled catalog or replace the dependency/feature manifests
required below. New bundled portable computations still follow the Wasm rule.

`ExternalLibraryDescriptor` distinguishes three profiles:

| Profile | Deployment and execution |
| --- | --- |
| `CoreWasm` | Memory-only libraries with an explicit Wasm ABI. Audio and MediaPipe pose implement their ABI 1; application dynamic modules can use the separate DynamicLibrary buffer ABI 1. General dependency linking remains future work. The CLR factory registry rejects this profile. |
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
label. New computational Emscripten adapters are rejected by the bundled CLR registry.
Applications can explicitly install a third-party DynamicLibrary package.

Jolt is distributed separately as `Aqua_JoltPhysics` 1.0.0, built from pinned
JoltPhysics.js 0.24.0. Its TOML manifest owns Aquarius bindings, bundled Emscripten
glue/Wasm and licenses. Desktop, browser and Android load it through the generic
DynamicLibrary factory ABI. Core has no Jolt descriptor, physics contracts, assets,
language catalog entries or default host registrations. World/body ownership and
numerical tests belong to that package. The Marble project imports
`/bottles/Aqua_JoltPhysics/main.aqua` and resolves it through Bottle.

`Aqua_OpenCV` is another independent Bottle library. Its native C++ bridge retains
matrix data behind handles; its portable backend uses real OpenCV Wasm. Backend
capability lists and measured kernel times are exposed to callers. Platform asset
filters select the implementation, and cache snapshots retain all declared
alternatives. See [Bottle](../AquariusBottle/README.md) and
[dynamic libraries](dynamic-linking.md).

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
