/* The overlay a standalone application carries (ExecutableBundle in the compiler): [wasm][JSON {"EntryPoint":...}]
   [64-byte footer: i32 version 2, i32 metadata length, i64 wasm length, SHA-256 of wasm and metadata,
   "AQUARIUS-APP-V2!"], appended to the launcher (Windows, Linux) or alone in a file of an app bundle (macOS, iOS). */
#ifndef AQUA_OVERLAY_H
#define AQUA_OVERLAY_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
/* The application at the end of the file `path`, or NULL; *damaged when an overlay is present but fails its checks.
   Only the overlay is read (a launcher before it is tens of megabytes). `entry` receives the overlay's entry script.
   The returned buffer holds the module (*wasm_length bytes) followed by the metadata and footer. */
uint8_t *overlay_read(const char *path, size_t *wasm_length, char **entry, bool *damaged);
#endif
