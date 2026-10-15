# Jolt Physics and Emscripten hosting

Jolt is distributed separately as the Aqua_JoltPhysics Bottle package, using
jolt-physics 0.24.0 from JoltPhysics.js. Core and the default runtime no longer
embed its binary, glue or bindings. The package owns the MIT engine notices.

Desktop hosts Emscripten using ClearScript/V8. Its ClearScript and V8 licenses are
included under licenses/clearscript. Aquarius application code uses Wasmtime,
licensed under Apache-2.0 WITH LLVM-exception; see WASMTIME-NOTICES.md.

Upstream:
- https://github.com/jrouwe/JoltPhysics.js
- https://github.com/jrouwe/JoltPhysics
- https://github.com/ClearFoundry/ClearScript
- https://github.com/bytecodealliance/wasmtime-dotnet
