# Patches to ONNX Runtime 1.30.0 for the web build

The web target compiles ONNX Runtime's source release (`cmake/onnxruntime-web.cmake`) with the runtime's toolchain:
Clang for `wasm32-wasip1`, position independent, no C++ exceptions, no threads and no Emscripten. ONNX Runtime's own
WebAssembly build assumes Emscripten and allows disabling exceptions only in minimal builds, which load only the ORT
model format; the old examples use ONNX models, so this is a full build. These patches are applied in order with
`patch -p1` to the extracted source tree; each file starts with its explanation.

| Patch | What and why |
| --- | --- |
| `0001-wasi-toolchain.patch` | `onnxruntime_WASI` for WebAssembly without Emscripten; the shared-provider bridge (dlopen) is left out as in minimal builds, with the minimal builds' stubs; exceptions may be disabled in a full build; ONNX's `tensor.h` throws through `ONNX_THROW_EX` (one hunk added to ONNX Runtime's patch for ONNX). |
| `0002-emscripten-only-code.patch` | Code using Emscripten APIs was keyed on `__wasm__`; it is keyed on `__EMSCRIPTEN__`, so other WebAssembly toolchains use the portable paths. No `dladdr` on WASI. |
| `0003-no-exceptions.patch` | Files only full builds compile: `gsl::narrow` (exceptions only) becomes `onnxruntime::narrow`, raw `try`/`catch` become `ORT_TRY`/`ORT_CATCH`, JSON parsing in the model-package code uses the non-throwing parse. |

Without exceptions, an error ONNX Runtime reports by throwing (for example a kernel's consistency check failing on a
malformed model) ends the application with a message on the console instead of failing the call; models that load and
run natively behave the same. The library validates inputs against the model's metadata before every run.

When updating ONNX Runtime, re-create the patches against the new release (the hunks are small and local), update the
version and hashes in `CMakeLists.txt` and `cmake/onnxruntime-web.cmake` (the dependency list follows ONNX Runtime's
`cmake/deps.txt`), and rerun the web tests.
