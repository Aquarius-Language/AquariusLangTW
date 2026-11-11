/* The export table read from an application module's aquarius.exports section (the format of ExportTable.Encode in
   the compiler): the web's embed runtime builds its table from it (web.c), since no per-library C is compiled there,
   and describes it to the glue, which makes the JavaScript functions (web-glue/core/library.mjs). */
#include "table.h"
#include <stdlib.h>
#include <string.h>

typedef struct reader { const uint8_t *p, *end; bool failed; } reader;

static uint32_t number(reader *r) {
    uint32_t value = 0;
    for (int shift = 0; shift < 35; shift += 7) {
        if (r->p >= r->end) { r->failed = true; return 0; }
        uint8_t b = *r->p++;
        value |= (uint32_t)(b & 0x7f) << shift;
        if (!(b & 0x80)) return value;
    }
    r->failed = true;
    return 0;
}
static uint8_t byte(reader *r) { if (r->p >= r->end) { r->failed = true; return 0; } return *r->p++; }
static char *text(reader *r) {
    uint32_t n = number(r);
    if (r->failed || n > (size_t)(r->end - r->p)) { r->failed = true; return calloc(1, 1); }
    char *s = malloc(n + 1);
    memcpy(s, r->p, n); s[n] = 0;
    r->p += n;
    return s;
}
static embed_type type(reader *r) {
    embed_type t = {0};
    t.shape = byte(r); t.scalar = byte(r);
    if (t.shape == EMBED_STRUCT) t.structure = number(r);
    return t;
}

/* The payload of the custom section `name`, or NULL. */
static const uint8_t *section(const uint8_t *module, size_t length, const char *name, size_t *size) {
    reader r = {module + 8, module + length, false};
    if (length < 8) return NULL;
    while (r.p < r.end) {
        uint8_t id = byte(&r);
        uint32_t n = number(&r);
        if (r.failed || n > (size_t)(r.end - r.p)) return NULL;
        const uint8_t *next = r.p + n;
        if (id == 0) {
            reader inner = {r.p, next, false};
            uint32_t k = number(&inner);
            if (!inner.failed && k <= (size_t)(next - inner.p) && k == strlen(name) && memcmp(inner.p, name, k) == 0) {
                *size = (size_t)(next - (inner.p + k));
                return inner.p + k;
            }
        }
        r.p = next;
    }
    return NULL;
}

bool embed_table_read(embed_description *d, const uint8_t *module, size_t length, const char **error) {
    memset(d, 0, sizeof *d);
    size_t size = 0;
    const uint8_t *payload = section(module, length, "aquarius.exports", &size);
    if (!payload) { *error = "The module exports nothing (no aquarius.exports section)."; return false; }
    reader r = {payload, payload + size, false};
    if (size < 5 || memcmp(payload, "AQEX", 4) != 0) { *error = "Invalid aquarius.exports section."; return false; }
    r.p += 4;
    if (byte(&r) != EMBED_TABLE_VERSION) { *error = "Unsupported aquarius.exports version."; return false; }
    free(text(&r)); /* the module path */
    d->struct_count = number(&r);
    if (r.failed || d->struct_count > 100000) { *error = "Invalid aquarius.exports section."; return false; }
    d->structs = calloc(d->struct_count + 1, sizeof *d->structs);
    for (uint32_t s = 0; s < d->struct_count && !r.failed; s++) {
        embed_struct *x = &d->structs[s];
        x->name = text(&r); x->binding = text(&r); free(text(&r));
        x->field_count = number(&r);
        if (x->field_count > 10000) { r.failed = true; break; }
        x->fields = calloc(x->field_count + 1, sizeof *x->fields);
        for (uint32_t f = 0; f < x->field_count && !r.failed; f++) { x->fields[f].name = text(&r); x->fields[f].type = type(&r); }
    }
    d->function_count = number(&r);
    if (r.failed || d->function_count > 100000) { *error = "Invalid aquarius.exports section."; return false; }
    d->functions = calloc(d->function_count + 1, sizeof *d->functions);
    for (uint32_t i = 0; i < d->function_count && !r.failed; i++) {
        embed_function *f = &d->functions[i];
        f->name = text(&r); f->binding = text(&r); free(text(&r));
        f->parameter_count = number(&r);
        if (f->parameter_count > 10000) { r.failed = true; break; }
        f->parameters = calloc(f->parameter_count + 1, sizeof *f->parameters);
        for (uint32_t p = 0; p < f->parameter_count && !r.failed; p++) { f->parameters[p].name = text(&r); f->parameters[p].type = type(&r); }
        f->result = type(&r);
    }
    if (r.failed) { *error = "Truncated aquarius.exports section."; return false; }
    return true;
}
