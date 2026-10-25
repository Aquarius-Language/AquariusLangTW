/* Resources scripts name by path (images, shaders, textures, meshes) and files they save.
 *
 * A path names a packaged asset when it is package-relative, under the application directory (目前工作目錄 + "/x")
 * or under /bottles/; otherwise, on targets with a file system, a file. Saving writes a file (on the web the browser
 * downloads it under the file's name, as the earlier web runtime did). */
#ifndef GRAPHICS_FILES_H
#define GRAPHICS_FILES_H

#include <aqua/aqua.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef struct gfx_bytes { const uint8_t *data; size_t length; bool owned; } gfx_bytes;

/* Reads the resource at `path`; on failure writes a message ("Could not find file '...'.") into `error`. */
bool gfx_read_resource(aqua_runtime *rt, const char *path, gfx_bytes *out, char *error, size_t error_size);
void gfx_bytes_free(gfx_bytes *bytes);
/* Writes `length` bytes to the file at `path`, creating its directory. */
bool gfx_write_file(const char *path, const uint8_t *data, size_t length, char *error, size_t error_size);
/* The lowercase extension of `path` including the dot (".png"), or "". */
const char *gfx_extension(const char *path, char *buffer, size_t size);

#endif
