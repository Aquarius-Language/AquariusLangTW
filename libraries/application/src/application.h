/* Internal declarations shared by the application library's sources. Every non-static name starts with app_ so the
 * library never clashes with the runtime or another default library linked into the same executable. */
#ifndef APP_APPLICATION_H
#define APP_APPLICATION_H

#include <aqua/aqua.h>
#include <aqua/image.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* ---- Limits (the old ApplicationLimits defaults) --------------------------------------------------------------- */
enum {
    APP_MAX_BYTES = 64 * 1024 * 1024,   /* encoded, expanded and transferred bytes */
    APP_MAX_DIMENSION = 8192,           /* image width or height */
    APP_MAX_PIXELS = 16 * 1024 * 1024,  /* pixels of all frames */
    APP_MAX_FRAMES = 256,
    APP_MAX_ENTRIES = 4096,             /* archive entries, directory entries, accessibility nodes */
    APP_MAX_DEPTH = 64,                 /* JSON nesting */
    APP_MAX_EDITOR = 1024 * 1024,       /* editor text in UTF-16 units */
};

/* ---- Failures: "Name: Kind: message", as the old runtime reported them ------------------------------------------ */
typedef enum app_kind {
    APP_UNSUPPORTED, APP_PERMISSION_DENIED, APP_UNAVAILABLE, APP_CANCELLED, APP_INVALID_DATA, APP_LIMIT_EXCEEDED,
    APP_NOT_FOUND, APP_CONFLICT
} app_kind;
typedef struct app_error { bool failed; app_kind kind; char message[700]; } app_error;
const char *app_kind_name(app_kind kind);
/* Records a failure and returns false, so operations can `return app_error_set(e, ...)`. */
bool app_error_set(app_error *e, app_kind kind, const char *format, ...) __attribute__((format(printf, 3, 4)));
bool app_check_bytes(uint64_t count, app_error *e);                 /* LimitExceeded beyond APP_MAX_BYTES */
bool app_check_pixels(int64_t width, int64_t height, int64_t frames, app_error *e);

/* The English name of the library function running on this thread ("ReadBytes"), set by library.c's wrappers. */
#if defined(AQUA_OS_WEB)
#define APP_THREAD_LOCAL /* one thread */
#else
#define APP_THREAD_LOCAL _Thread_local
#endif
extern APP_THREAD_LOCAL const char *app_function;
void app_fail(aqua_call *call, const app_error *e);
void app_fail_with(aqua_call *call, app_kind kind, const char *format, ...) __attribute__((format(printf, 3, 4)));

/* ---- Bytes ------------------------------------------------------------------------------------------------------ */
typedef struct app_bytes { uint8_t *data; size_t length, capacity; } app_bytes;
bool app_bytes_reserve(app_bytes *b, size_t capacity);
bool app_bytes_append(app_bytes *b, const void *data, size_t count);
bool app_bytes_byte(app_bytes *b, uint8_t value);
bool app_bytes_text(app_bytes *b, const char *text);                 /* appends without the NUL */
void app_bytes_free(app_bytes *b);
static inline void app_put16le(uint8_t *p, uint32_t v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); }
static inline void app_put32le(uint8_t *p, uint32_t v) { for (int i = 0; i < 4; i++) p[i] = (uint8_t)(v >> (8 * i)); }
static inline void app_put32be(uint8_t *p, uint32_t v) { for (int i = 0; i < 4; i++) p[i] = (uint8_t)(v >> (24 - 8 * i)); }
static inline uint32_t app_get16le(const uint8_t *p) { return (uint32_t)p[0] | (uint32_t)p[1] << 8; }
static inline uint32_t app_get32le(const uint8_t *p) { return (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24; }
static inline uint32_t app_get16be(const uint8_t *p) { return (uint32_t)p[0] << 8 | (uint32_t)p[1]; }
static inline uint32_t app_get32be(const uint8_t *p) { return (uint32_t)p[0] << 24 | (uint32_t)p[1] << 16 | (uint32_t)p[2] << 8 | (uint32_t)p[3]; }
uint32_t app_crc32(uint32_t crc, const uint8_t *data, size_t length); /* standard CRC-32, continued from crc */

/* Byte arrays: integers 0..255 of at most APP_MAX_BYTES elements. */
bool app_value_bytes(aqua_runtime *rt, aqua_value array, app_bytes *out, app_error *e);
aqua_value app_bytes_value(aqua_runtime *rt, const uint8_t *data, size_t length);
char *app_strdup(const char *text);
char *app_strndup(const char *text, size_t length);
char *app_printf(const char *format, ...) __attribute__((format(printf, 1, 2)));
bool app_equals_ignore_case(const char *a, const char *b);         /* ASCII case-insensitive */
void app_random_hex(char *out, size_t digits);                      /* lowercase hex digits from the OS random source */

/* ---- Values: records, hashes, numbers -------------------------------------------------------------------------- */
/* A record: a native object whose fields read the hash it wraps (the old runtime's field records: info.width). */
aqua_value app_record(aqua_runtime *rt, aqua_value hash);
void app_set(aqua_runtime *rt, aqua_value hash, const char *field, aqua_value value);
void app_set_text(aqua_runtime *rt, aqua_value hash, const char *field, const char *text); /* NULL sets null */
aqua_value app_number(double n);                                     /* int when integral and in int32 range */
aqua_value app_capability(aqua_runtime *rt, bool available, const char *restriction);
bool app_field(aqua_runtime *rt, aqua_value hash, const char *name, aqua_value *out);   /* case-insensitive key */

/* ---- JSON (Serialization.Json rules: null, Boolean, finite numbers, strings, arrays, string-keyed hashes) ------- */
bool app_json_write(aqua_runtime *rt, aqua_value value, app_bytes *out, app_error *e);
bool app_json_parse(aqua_runtime *rt, const uint8_t *text, size_t length, aqua_value *out, app_error *e);

/* ---- AQD1 documents: "AQD1", uint32 schema, uint32 JSON length, CRC-32, strict UTF-8 JSON (little-endian) ------ */
bool app_document_encode(aqua_runtime *rt, aqua_value value, uint32_t schema, app_bytes *out, app_error *e);
bool app_document_decode(aqua_runtime *rt, const uint8_t *data, size_t length, uint32_t *schema, aqua_value *value, app_error *e);

/* ---- Compression (compression.c) and ZIP archives (archive.c) --------------------------------------------------- */
typedef enum app_compression { APP_GZIP, APP_ZLIB, APP_DEFLATE, APP_BROTLI } app_compression;
typedef enum app_level { APP_OPTIMAL, APP_FASTEST, APP_SMALLEST, APP_NO_COMPRESSION } app_level;
bool app_compression_of(const char *name, app_compression *out, app_error *e);
bool app_level_of(const char *name, app_level *out, app_error *e);
bool app_compress(const uint8_t *data, size_t length, app_compression format, app_level level, app_bytes *out, app_error *e);
/* Expands at most `limit` bytes; container checksums, headers and lengths are validated. */
bool app_decompress(const uint8_t *data, size_t length, app_compression format, size_t limit, app_bytes *out, app_error *e);
/* A zlib stream at zlib level 0..9 (PNG and TIFF writers). */
bool app_zlib(const uint8_t *data, size_t length, int level, app_bytes *out, app_error *e);
typedef struct app_entry { char *name; app_bytes data; } app_entry;
bool app_archive_name_valid(const char *name, size_t length);
bool app_archive_create(const app_entry *entries, size_t count, app_bytes *out, app_error *e);
bool app_archive_read(const uint8_t *data, size_t length, app_entry **entries, size_t *count, app_error *e);
void app_entries_free(app_entry *entries, size_t count);

/* ---- Text encodings: strict and BOM-free (utf-8, utf-16le, utf-16be, ascii) ------------------------------------- */
typedef enum app_encoding { APP_UTF8, APP_UTF16LE, APP_UTF16BE, APP_ASCII } app_encoding;
bool app_encoding_of(const char *name, app_encoding *out, app_error *e);
bool app_text_encode(aqua_runtime *rt, aqua_value text, app_encoding encoding, app_bytes *out, app_error *e);
bool app_text_decode(aqua_runtime *rt, const uint8_t *data, size_t length, app_encoding encoding, aqua_value *out, app_error *e);
bool app_utf8_valid(const uint8_t *data, size_t length);
/* UTF-16 code units of a string value, copied (free with aqua_free); NULL for a non-string. */
uint16_t *app_text16(aqua_runtime *rt, aqua_value text, size_t *count);

/* ---- Cancellation and calls that finish later ----------------------------------------------------------------- */
typedef struct app_token { uint32_t references; bool cancelled; } app_token;
void app_token_release(app_token *token);

struct app_state;
/* A library call that may complete after its function returned (web storage, pickers, the clipboard). Operations
   take a task and finish it once: synchronously on native targets (the function returns the result), or later from a
   Web API callback (the script waits on a deferred result). */
typedef struct app_task app_task;
struct app_task {
    aqua_runtime *rt;
    struct app_state *state;
    aqua_call *call;            /* while the library function runs */
    aqua_pending *pending;      /* once it returned before finishing */
    const char *name;           /* app_function at the start */
    app_token *token;           /* Tasks.UseCancellation token when the call started */
    bool finished;
    void (*cleanup)(app_task *task); /* frees the operation's own fields */
    void *backend;              /* a backend operation chaining several steps (web copies, streams) */
};
/* A task of `size` bytes (a struct whose first member is app_task), zeroed. */
void *app_task_new(aqua_call *call, size_t size);
void app_task_return(app_task *task, aqua_value result);
void app_task_fail(app_task *task, const app_error *e);
void app_task_error(app_task *task, app_kind kind, const char *format, ...) __attribute__((format(printf, 3, 4)));
/* Ends the library function: frees a finished task or makes the script wait for it. */
void app_task_yield(app_task *task);
/* Whether the call's cancellation token was cancelled; fails the task with Cancelled when it was. */
bool app_task_cancelled(app_task *task);
bool app_cancelled(aqua_call *call);                                /* same for synchronous functions */

/* ---- Resources ------------------------------------------------------------------------------------------------ */
typedef enum app_backend {
    APP_PATH,        /* an operating system path */
    APP_PERSISTENT,  /* web: persistent:/path, an IndexedDB record */
    APP_DOWNLOAD,    /* web: download:/name, saved through a download */
    APP_TEMPORARY,   /* web: temporary:/lease/child, kept in memory for the run */
    APP_SELECTED,    /* web: a picked File or FileSystemHandle */
    APP_DOCUMENT,    /* Android: a content:// document from a picker or the launching intent */
} app_backend;
enum { APP_FILE = 0, APP_DIRECTORY = 1, APP_ASSET = 2 };
typedef struct app_resource {
    char *provider, *id, *name, *identity; /* identity: persistentIdentity, or NULL */
    int32_t kind;
    app_backend backend;
    char *location;   /* OS path, store path, document URI */
    int32_t handle;   /* web: the glue handle of a selected resource */
    uint32_t lease;   /* the temporary lease this resource is (Temporary()), 0 otherwise */
} app_resource;
/* A resource object holding a copy of `resource`. */
aqua_value app_resource_value(aqua_runtime *rt, const app_resource *resource);
/* The resource argument at `index`, after failing the call when it is not one of this runtime's resources. */
app_resource *app_resource_arg(aqua_call *call, uint32_t index);
app_resource app_resource_copy(const app_resource *resource);
void app_resource_free(app_resource *resource);
/* A record of a resource ({provider, id, name, kind, persistentIdentity}) for Storage.Recent. */
aqua_value app_resource_descriptor(aqua_runtime *rt, const app_resource *resource);

/* ---- Per-runtime state ----------------------------------------------------------------------------------------- */
typedef struct app_lease {
    uint32_t id;
    bool directory, disposed;
    char *location;            /* the OS path, or the web temporary name */
    struct app_lease *next;
} app_lease;
typedef struct app_stream app_stream;
typedef struct app_state {
    aqua_runtime *rt;
    char provider[48];         /* "desktop-<32 hex>" or "browser-<32 hex>": resources of this runtime */
    app_token *cancellation;   /* Tasks.UseCancellation */
    app_lease *leases;         /* temporary resources, removed at disposal or when the runtime ends */
    uint32_t next_lease;
    app_stream *streams;       /* open streams, closed when the runtime ends */
    void *files;               /* the files backend's data (web virtual files) */
    void *platform;            /* the OS layer's data (accessibility, clipboard owner, web database) */
} app_state;
app_state *app_state_of(aqua_call *call);
void app_state_close_streams(app_state *state);

/* ---- Classes registered by library.c ---------------------------------------------------------------------------- */
extern aqua_class *app_record_class, *app_resource_class, *app_stream_class, *app_editor_class, *app_token_class;

/* ---- Images (images.c): the documented RGBA8 image and its metadata -------------------------------------------- */
typedef enum app_format { APP_PNG, APP_JPEG, APP_BMP, APP_GIF, APP_TIFF, APP_FORMAT_COUNT } app_format;
extern const char *const app_format_names[APP_FORMAT_COUNT];
typedef struct app_metadata {
    int32_t orientation;          /* EXIF orientation 1..8 */
    double dpi_x, dpi_y;          /* 0 when unknown */
    char source[64];              /* source pixel format: "TrueColorAlpha, 8 bits/channel" */
    uint32_t *palette;            /* RGBA colors packed R<<24|G<<16|B<<8|A, or NULL */
    uint32_t palette_count;
} app_metadata;
typedef struct app_picture {      /* a decoded frame: straight RGBA8, top-down */
    int32_t width, height;
    uint8_t *rgba;
    app_metadata metadata;
} app_picture;
typedef struct app_image_info { app_format format; int32_t width, height, frames; app_metadata metadata; } app_image_info;
void app_picture_free(app_picture *picture);
bool app_identify(const uint8_t *data, size_t length, app_format *format, app_error *e);
bool app_image_inspect(const uint8_t *data, size_t length, app_image_info *info, app_error *e);
bool app_image_decode(const uint8_t *data, size_t length, int32_t frame, app_picture *out, app_error *e);
/* Encoding options ({quality, compression, background, allowMetadataLoss}). */
typedef struct app_encode_options { int32_t quality, compression; bool has_background; uint32_t background; bool allow_metadata_loss; } app_encode_options;
void app_encode_defaults(app_encode_options *options);
bool app_encode_options_of(aqua_runtime *rt, aqua_value value, app_encode_options *options, app_error *e);
/* Straight RGBA and metadata of an image object (core aqua_image), validated. */
bool app_picture_of(aqua_runtime *rt, aqua_value image, app_picture *out, app_error *e);
bool app_image_encode(const app_picture *picture, app_format format, const app_encode_options *options, app_bytes *out, app_error *e);
/* A new image object (core aqua_image) of a decoded picture; takes the pixels. */
aqua_value app_picture_value(aqua_runtime *rt, app_picture *picture);
aqua_value app_metadata_value(aqua_runtime *rt, const app_metadata *metadata, bool palette);

#endif
