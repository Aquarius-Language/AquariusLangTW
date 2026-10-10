# Jolt Physics and Emscripten hosting

Desktop and browser use jolt-physics 0.24.0 from JoltPhysics.js, with the binary,
generated glue and shared physics code embedded by AquariusCore. Jolt is MIT
licensed. JOLT-WASM-LICENSE.txt is copied from the pinned npm dependency.

Desktop hosts Emscripten using ClearScript/V8. Its ClearScript and V8 licenses are
included under licenses/clearscript. Aquarius application code uses Wasmtime,
licensed under Apache-2.0 WITH LLVM-exception; see WASMTIME-NOTICES.md.

Upstream:
- https://github.com/jrouwe/JoltPhysics.js
- https://github.com/jrouwe/JoltPhysics
- https://github.com/ClearFoundry/ClearScript
- https://github.com/bytecodealliance/wasmtime-dotnet
