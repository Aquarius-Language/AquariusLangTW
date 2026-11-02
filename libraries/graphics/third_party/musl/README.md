# musl libm subset (Processing and GLM math)

Processing's `sin`, `cos`, `tan`, `asin`, `acos`, `atan`, `atan2`, `exp`, `log` and `pow` (and GLM's `Sin` and `Cos`) are
these musl functions, the same code the guest runtime's portable `數學` / `Math` module runs
(`runtime/guest/third_party/musl`), so every target gives the same results as `Math` bit for bit
(`tests/processing_math_parity.aqua`). The C libraries of the targets (MinGW, glibc, bionic, the web's libc) differ in
the last bits of some results.

- Version: musl 1.2.5, `https://musl.libc.org/releases/musl-1.2.5.tar.gz`,
  SHA-256 `a9a118bbe84d8764da0ea0d28b3ab3fae8477fc7e4085d90102b8596fc7c75e4`.
- License: MIT (`COPYRIGHT`, unchanged from the release).
- `src/internal/libm.h` and `src/math/*.c|h` are unchanged copies of the release's files.
- `shim/` replaces the headers musl generates per architecture; `aqua_libm.h` renames every symbol to
  `aqua_graphics_*`, since the library links statically into the runtime beside the C library's libm. `CMakeLists.txt`
  compiles the files with it, without fused multiply-adds (`-ffp-contract=off`, `-U__FP_FAST_FMA`), and
  `src/processing/math.c` and `src/modules/glm.c` include it after `<math.h>`.
