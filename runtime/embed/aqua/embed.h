/* aqua/embed.h: the embed runtime of native library outputs (docs/native-libraries.md), as the per-library half
   (exports.c, expanding the compiler-written exports.def) calls it. A library holds one application instance per
   process: every call takes one internal lock, so calls from several threads run one at a time; a call from inside a
   call on the same thread is refused (reentrancy). The last error is kept per thread. None of these functions is
   exported from the library: only the <prefix>* functions of exports.c are.

   A call is: aqua_embed_begin(function), one aqua_embed_put_* per argument (a struct argument is its fields followed
   by aqua_embed_put_struct), aqua_embed_invoke(), the result's aqua_embed_* getter when it succeeded, aqua_embed_end().
   Conversions that fail (invalid UTF-8, NULL data with a nonzero count) make aqua_embed_invoke fail without calling. */
#ifndef AQUA_EMBED_H
#define AQUA_EMBED_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* The ABI conventions of every library's C header (<prefix>abi_version(); CBinding.AbiVersion in the compiler). */
#define AQUA_EMBED_ABI_VERSION 1

/* What the per-library half tells the runtime: the Aquarius names of its exported functions and of its exported
   structs (whose hidden constructors are "export@" + name), the default libraries linked into it, and its application
   module. */
typedef struct aqua_library aqua_library;
typedef struct aqua_embed_library { const char *name; void (*entry)(aqua_library *library); } aqua_embed_library;
typedef struct aqua_embed_table {
    const char *const *functions; uint32_t function_count;
    const char *const *structs; const uint32_t *field_counts; uint32_t struct_count;
    const aqua_embed_library *libraries; uint32_t library_count;
    const unsigned char *module; size_t module_size;
} aqua_embed_table;
extern const aqua_embed_table aqua_embed_exports; /* exports.c */

/* The element kinds of arrays. */
typedef enum aqua_embed_element { AQUA_EMBED_INT, AQUA_EMBED_LONG, AQUA_EMBED_FLOAT, AQUA_EMBED_DOUBLE, AQUA_EMBED_BOOL } aqua_embed_element;

bool aqua_embed_initialize(void);
void aqua_embed_shutdown(void);
const char *aqua_embed_last_error(void);

bool aqua_embed_begin(uint32_t function);
void aqua_embed_put_int(int32_t value);
void aqua_embed_put_long(int64_t value);
void aqua_embed_put_float(float value);
void aqua_embed_put_double(double value);
void aqua_embed_put_bool(bool value);
void aqua_embed_put_string(const char *utf8);
void aqua_embed_put_array(const void *data, size_t count, aqua_embed_element element);
/* Replaces the last field_count arguments with the struct `structure` made from them. */
void aqua_embed_put_struct(uint32_t structure);
bool aqua_embed_invoke(void);
/* The result (field < 0) or field `field` of a struct result. Strings and array data are malloc'd copies the caller
   frees with <prefix>free. */
int32_t aqua_embed_int(int32_t field);
int64_t aqua_embed_long(int32_t field);
float aqua_embed_float(int32_t field);
double aqua_embed_double(int32_t field);
bool aqua_embed_bool(int32_t field);
char *aqua_embed_string(int32_t field);
void *aqua_embed_array(size_t *count, aqua_embed_element element);
void aqua_embed_end(void);

/* The platform layer's part (platform/<os>/ or platform/desktop/embed.c): writes buffered output after each call. */
void embed_platform_flush(void);
#endif
