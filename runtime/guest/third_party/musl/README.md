# Vendored musl libm subset

The guest runtime's `數學` / `Math` module (`runtime/guest/maths.h`) computes its transcendental functions with these
files of musl's libm, compiled beside `runtime.c` into `runtime.wasm` by `scripts/build-wasm-runtime.ps1` / `.sh`. The
guest runtime is the same Wasm on every target, so the results are bit-identical everywhere (Wasm floating-point
arithmetic is deterministic apart from NaN payloads).

- Version: musl 1.2.5, `https://musl.libc.org/releases/musl-1.2.5.tar.gz`,
  SHA-256 `a9a118bbe84d8764da0ea0d28b3ab3fae8477fc7e4085d90102b8596fc7c75e4`.
- License: MIT (`COPYRIGHT`, copied unchanged from the release).
- `src/internal/libm.h` and `src/math/*.c|h` are copied unchanged from the release's `src/internal` and `src/math`:
  `sin cos tan asin acos atan atan2 exp log log2 log10 pow cbrt hypot sinh cosh tanh fmod expm1 scalbn`, the kernels
  `__sin __cos __tan __rem_pio2 __rem_pio2_large __expo2`, the error helpers `__math_*` and the tables
  `exp_data log_data log2_data pow_data`.
- `shim/` replaces the headers musl generates per architecture (`math.h`, `features.h`, `endian.h`, `fp_arch.h`): only
  the declarations these sources use. `sqrt`, `fabs`, `floor`, `ceil` and `trunc` map to the Wasm instructions
  (correctly rounded), so musl's software versions are not needed.

Every symbol is internal to `runtime.wasm` (only `aqua_*` and `rt_*` functions are exported). To update, replace the
files from a newer release, update the version and SHA-256 above, rebuild `runtime.wasm` and run the Math tests
(`tests/math-library.json`).
