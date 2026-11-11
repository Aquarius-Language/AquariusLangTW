/* The export table of an application module (its aquarius.exports section), read by table.c for the web's embed
   runtime. Shapes and scalars are the compiler's ExportShape and ScalarType numbers. */
#ifndef AQUA_EMBED_TABLE_H
#define AQUA_EMBED_TABLE_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define EMBED_TABLE_VERSION 1
enum { EMBED_VOID = 0, EMBED_SCALAR = 1, EMBED_ARRAY = 2, EMBED_STRUCT = 3 };
enum { EMBED_INT = 1, EMBED_FLOAT = 2, EMBED_DOUBLE = 3, EMBED_BOOL = 4, EMBED_STRING = 5, EMBED_LONG = 6 };

typedef struct embed_type { uint8_t shape, scalar; uint32_t structure; } embed_type;
typedef struct embed_named { char *name; embed_type type; } embed_named;
typedef struct embed_struct { char *name, *binding; embed_named *fields; uint32_t field_count; } embed_struct;
typedef struct embed_function { char *name, *binding; embed_named *parameters; uint32_t parameter_count; embed_type result; } embed_function;
typedef struct embed_description {
    embed_struct *structs; uint32_t struct_count;
    embed_function *functions; uint32_t function_count;
} embed_description;

/* Reads the section of `module`; false with *error set when it is missing or malformed. */
bool embed_table_read(embed_description *description, const uint8_t *module, size_t length, const char **error);
#endif
