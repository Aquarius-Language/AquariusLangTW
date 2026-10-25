#include "records.h"
#include <stdlib.h>

static aqua_class *record_class;

typedef struct record { aqua_ref hash; } record;

static void record_finalize(aqua_runtime *rt, void *data) {
    record *r = data;
    aqua_release(rt, r->hash);
    free(r);
}

static void record_field(aqua_call *call, const char *name) {
    record *r = aqua_self(call);
    aqua_runtime *rt = aqua_call_runtime(call);
    aqua_value value;
    if (!r || !aqua_hash_field(rt, aqua_ref_value(rt, r->hash), name, &value)) { aqua_fail(call, "Module member not found: %s", name); return; }
    aqua_return(call, value);
}

#define RECORD_FIELD(name) static void field_##name(aqua_call *call) { record_field(call, #name); }
#include "records.def"
#undef RECORD_FIELD

aqua_value gfx_record_new(aqua_runtime *rt, aqua_value hash) {
    record *r = malloc(sizeof *r);
    r->hash = aqua_retain(rt, hash);
    return aqua_object_new(rt, record_class, r);
}

aqua_value gfx_record_of(aqua_runtime *rt, uint32_t count, const char *const *names, const aqua_value *values) {
    aqua_value hash = aqua_hash_new(rt, count);
    for (uint32_t i = 0; i < count; i++) aqua_hash_set_field(rt, hash, names[i], values[i]);
    return gfx_record_new(rt, hash);
}

void gfx_records_register(aqua_library *library) {
    record_class = aqua_define_class(library, "record", record_finalize);
#define RECORD_FIELD(name) aqua_define_property(record_class, #name, field_##name);
#include "records.def"
#undef RECORD_FIELD
}
