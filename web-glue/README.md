# AquariusLang web glue

The only JavaScript in AquariusLang. It does two things:

1. **Starts applications** (`core/`): instantiates `program.wasm`, links the host core (`aquarius.wasm`), the C/C++
   runtime libraries and every library side module into the application's memory (`core/loader.mjs`, the `dylink.0`
   convention), gives the C library its WASI calls and drives the host's loop from the browser's event loop.
2. **Exposes Web APIs** to C, C++ and Rust code compiled to WebAssembly, one domain folder per API.

It never implements library behavior: parsing, layout, math, protocol logic, caching policy and validation stay in
the libraries' C/C++ code, compiled to the same algorithm on every platform. A glue function translates arguments in
memory to one Web API call (or one asynchronous operation) and its result back.

## Layout

```text
web-glue/
  index.mjs          imports every domain; the page imports startApplication from here
  core/              loader, WASI, application start, the registry (glue.mjs)
  page/              index.html and app.mjs of exported websites
  window/            <canvas> windows and DOM input for aqua/window.h
  webgl/             WebGL 2 calls, typed pixel/buffer views and opaque GL object handles
  <domain>/          one folder per Web API: webgpu, webgl, canvas, audio, network, storage, ...
```

Each domain folder holds everything for its Web API, so the whole `web-glue` folder can move to its own repository.

## Adding a domain

```js
// web-glue/network/network.mjs
import { registerGlue } from '../core/glue.mjs';
registerGlue('network', (context) => ({
    net_fetch(url, length, done, data) { /* fetch(context.text(url, length)) ... then context.callPointer(done, data, status) */ },
}));
```

then add `import './network/network.mjs';` to `index.mjs`. Rules:

- Function names are `<domain>_<operation>`, except standard C APIs implemented under their own names (`webgpu.h`'s
  `wgpu*`, OpenGL ES's `gl*`), which C code calls without declarations.
- Arguments are numbers and pointers into the shared memory: read text with `context.text(pointer, length)`, bytes with
  `context.bytes`, write results into memory the C side passed, or allocate with `context.allocate` (C frees with
  `free`). JavaScript objects cross as handles from `context.retain(object)` / `context.object(handle)`.
- Asynchronous APIs take a C callback (a function pointer) and its data; call it with `context.callPointer` when the
  operation settles, then `context.wake()` so the runtime takes a turn. The C side resolves its deferred call there.
- No state beyond what the Web API needs (handles, event queues); no decisions a C implementation could make.

`network` exposes WebSocket subprotocol selection and the page host/port/security properties for C clients.
`webrtc` exposes peer connections, SDP operations, ICE candidates and data channels. Its asynchronous operations
return strings through C callbacks; peer/channel state, negotiation policy, JSON and event queues belong to the
calling library. Both domains are available to external SDK side modules through `aqua_web` imports.
