// This JS example transports bytes and static text; computations use library.c/Wasm.
export function aqua_library_abi() { return 1; }
export function aqua_create_library() {
  // The standalone module is intentionally dependency-free; the host supplies bytes.
  return {
    echo: input => input,
    greeting: () => new TextEncoder().encode('Hello from a dynamic library! / 動態函式庫向你問好！')
  };
}
