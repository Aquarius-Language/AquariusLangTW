/* The web's part of the embed runtime (docs/native-libraries.md), linked into the host side module (aquarius.wasm):
   the library's table comes from the loaded module's aquarius.exports section (table.c), and these functions describe
   it to the glue, which builds one JavaScript function per export (web-glue/core/library.mjs) calling embed.c's
   aqua_embed_* functions, which the side module exports as they are. Types are numbers: shape | scalar << 8 |
   struct << 16 (table.h). */
#include "../host/host.h"
#include "aqua/embed.h"
#include "table.h"
#include <stdlib.h>
#include <string.h>

#define WEB(name) __attribute__((export_name(#name)))

static embed_description description;
static aqua_embed_table table;
static bool loaded;
static application metadata; /* the module's metadata: the native libraries it needs */
static char **javascript_names; /* each function's JavaScript name */

const aqua_embed_table *aqua_embed_current_table(void) { return &table; }
void embed_platform_flush(void) {} /* the web writes each line as it is printed */

/* The JavaScript name of an export: lowerCamelCase of its public name (distance_to, DistanceTo and HTTPServer give
   distanceTo, distanceTo, httpServer), as ExportTable.LowerCamelCase in the compiler names it for the .d.ts. */
static char *lower_camel(const char *name) {
    size_t n = strlen(name);
    char *snake = malloc(2 * n + 1), *camel = malloc(n + 1);
    size_t s = 0, c = 0;
    for (size_t i = 0; i < n; i++) {
        char x = name[i];
        bool upper = x >= 'A' && x <= 'Z';
        bool before_upper = i > 0 && name[i - 1] >= 'A' && name[i - 1] <= 'Z';
        bool next_lower = i + 1 < n && name[i + 1] >= 'a' && name[i + 1] <= 'z';
        if (upper && i > 0 && name[i - 1] != '_' && (!before_upper || next_lower)) snake[s++] = '_';
        snake[s++] = upper ? (char)(x - 'A' + 'a') : x;
    }
    bool capital = false;
    for (size_t i = 0; i < s; i++) {
        if (snake[i] == '_') { capital = c > 0; continue; }
        camel[c++] = capital && snake[i] >= 'a' && snake[i] <= 'z' ? (char)(snake[i] - 'a' + 'A') : snake[i];
        capital = false;
    }
    camel[c] = 0;
    free(snake);
    return camel;
}

/* Reads the table of the application module at [module, module + length), which the glue keeps for the page's life.
   Returns NULL, or why it cannot be a library. */
WEB(aqua_web_library_load) const char *aqua_web_library_load(const uint8_t *module, size_t length) {
    if (loaded) return "This page has loaded an AquariusLang library already.";
    const char *error = NULL;
    if (!embed_table_read(&description, module, length, &error)) return error;
    uint8_t *copy = malloc(length);
    memcpy(copy, module, length);
    char *failure = NULL;
    if (!application_parse(&metadata, copy, length, &failure)) { free(copy); return failure ? failure : "Invalid application metadata."; }
    const char **functions = calloc(description.function_count + 1, sizeof *functions);
    for (uint32_t i = 0; i < description.function_count; i++) functions[i] = description.functions[i].binding;
    const char **structs = calloc(description.struct_count + 1, sizeof *structs);
    uint32_t *fields = calloc(description.struct_count + 1, sizeof *fields);
    for (uint32_t s = 0; s < description.struct_count; s++) { structs[s] = description.structs[s].binding; fields[s] = description.structs[s].field_count; }
    table = (aqua_embed_table){functions, description.function_count, structs, fields, description.struct_count, NULL, 0, module, length};
    javascript_names = calloc(description.function_count + 1, sizeof *javascript_names);
    for (uint32_t i = 0; i < description.function_count; i++) javascript_names[i] = lower_camel(description.functions[i].name);
    loaded = true;
    return NULL;
}

/* The native libraries the application needs (its metadata): the glue links libraries/<name>.wasm for each first. */
WEB(aqua_web_library_native_count) uint32_t native_count(void) { return metadata.library_count; }
WEB(aqua_web_library_native_name) const char *native_name(uint32_t i) { return i < metadata.library_count ? metadata.libraries[i] : NULL; }
static int32_t code(embed_type t) { return (int32_t)(t.shape | t.scalar << 8 | t.structure << 16); }
static embed_function *function(uint32_t i) { return i < description.function_count ? &description.functions[i] : NULL; }
static embed_struct *structure(uint32_t s) { return s < description.struct_count ? &description.structs[s] : NULL; }

WEB(aqua_web_library_abi_version) uint32_t abi_version(void) { return AQUA_EMBED_ABI_VERSION; }
WEB(aqua_web_library_function_count) uint32_t function_count(void) { return description.function_count; }
WEB(aqua_web_library_function_name) const char *function_name(uint32_t i) { return function(i) ? function(i)->name : NULL; }
WEB(aqua_web_library_function_javascript_name) const char *function_javascript_name(uint32_t i) { return function(i) ? javascript_names[i] : NULL; }
WEB(aqua_web_library_parameter_count) uint32_t parameter_count(uint32_t i) { return function(i) ? function(i)->parameter_count : 0; }
WEB(aqua_web_library_parameter_name) const char *parameter_name(uint32_t i, uint32_t p) {
    return function(i) && p < function(i)->parameter_count ? function(i)->parameters[p].name : NULL;
}
WEB(aqua_web_library_parameter_type) int32_t parameter_type(uint32_t i, uint32_t p) {
    return function(i) && p < function(i)->parameter_count ? code(function(i)->parameters[p].type) : 0;
}
WEB(aqua_web_library_result_type) int32_t result_type(uint32_t i) { return function(i) ? code(function(i)->result) : 0; }
WEB(aqua_web_library_struct_count) uint32_t struct_count(void) { return description.struct_count; }
WEB(aqua_web_library_struct_name) const char *struct_name(uint32_t s) { return structure(s) ? structure(s)->name : NULL; }
WEB(aqua_web_library_field_count) uint32_t field_count(uint32_t s) { return structure(s) ? structure(s)->field_count : 0; }
WEB(aqua_web_library_field_name) const char *field_name(uint32_t s, uint32_t f) {
    return structure(s) && f < structure(s)->field_count ? structure(s)->fields[f].name : NULL;
}
WEB(aqua_web_library_field_type) int32_t field_type(uint32_t s, uint32_t f) {
    return structure(s) && f < structure(s)->field_count ? code(structure(s)->fields[f].type) : 0;
}
