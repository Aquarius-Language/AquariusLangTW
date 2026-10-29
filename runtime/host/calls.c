/* Calls of library functions: argument checking against the registered signature, the aqua_call API, results and
   errors. */
#include "host.h"
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static const char *kind_name(char kind) {
    switch (kind) {
    case 'n': return "a number"; case 'i': return "an integer"; case 'b': return "a boolean"; case 's': return "a string";
    case 'a': return "an array"; case 'h': return "a hash"; case 'f': return "a function"; case 'o': return "a native object";
    default: return "a value";
    }
}
static bool kind_accepts(char kind, aqua_value v) {
    switch (kind) {
    case 'n': return aqua_is_number(v);
    case 'i': return v.type == AQUA_INT || (aqua_is_number(v) && v.number == floor(v.number) && v.number >= -2147483648.0 && v.number <= 2147483647.0);
    case 'b': return v.type == AQUA_BOOL;
    case 's': return v.type == AQUA_STRING;
    case 'a': return v.type == AQUA_ARRAY;
    case 'h': return v.type == AQUA_HASH;
    case 'f': return aqua_is_callable(v);
    case 'o': return v.type == AQUA_NATIVE;
    default: return true;
    }
}

/* "Module.name / 模組.名稱" for messages. */
static void call_name(const aqua_class *owner, const member *m, char *out, size_t size) {
    char owner_name[64], member_name[64];
    names_first(owner->names, owner_name, sizeof owner_name);
    names_first(m->names, member_name, sizeof member_name);
    snprintf(out, size, "%s.%s", owner_name, member_name);
}

static bool check_arguments(aqua_call *call) {
    const member *m = call->member;
    char name[160];
    if (call->count < m->minimum || call->count > m->maximum) {
        call_name(call->owner, m, name, sizeof name);
        if (m->maximum == UINT32_MAX) aqua_fail(call, "%s expects at least %u arguments, got %u.", name, m->minimum, call->count);
        else if (m->minimum == m->maximum) aqua_fail(call, "%s expects %u argument%s, got %u.", name, m->minimum, m->minimum == 1 ? "" : "s", call->count);
        else aqua_fail(call, "%s expects %u to %u arguments, got %u.", name, m->minimum, m->maximum, call->count);
        return false;
    }
    const char *p = m->signature;
    char repeat = 0;
    bool has_optional = strchr(m->signature, '|') != NULL;
    for (uint32_t i = 0; i < call->count; i++) {
        char kind = repeat;
        if (!kind) {
            while (*p == '|') p++;
            kind = *p ? *p++ : 'v';
            if (*p == '*') { repeat = kind; p++; } /* a repeated kind covers every remaining argument */
        }
        aqua_value v = call->arguments[i];
        /* Optional arguments may be null to mean "not given". */
        if (has_optional && i >= m->minimum && aqua_is_nullish(v)) continue;
        if (!kind_accepts(kind, v)) {
            call_name(call->owner, m, name, sizeof name);
            aqua_fail(call, "%s: argument %u must be %s, got %s.", name, i + 1, kind_name(kind), aqua_type_name(v));
            return false;
        }
    }
    return true;
}

aqua_value call_native(aqua_runtime *rt, uint32_t function_handle, const aqua_value *arguments, uint32_t count, uint32_t context, aqua_pending **pending) {
    handle *h = handle_get(rt, function_handle);
    if (!h || h->kind != HANDLE_BOUND) return host_error(rt, "Not a function: %s", h && h->cls ? h->cls->names : "native value");
    handle *self = handle_get(rt, h->self);
    aqua_call call = {0};
    call.rt = rt; call.owner = self->cls; call.member = &self->cls->members[h->member];
    call.self = h->self; call.context = context; call.arguments = arguments; call.count = count;
    call.result = aqua_void();
    if (check_arguments(&call)) {
        call.member->function(&call);
        /* An async function gives a promise of its outcome instead of making its caller wait. */
        if (call.member->async) { call.result = promise_of_call(rt, &call); call.pending = NULL; }
    }
    for (scratch_block *b = call.scratch; b;) { scratch_block *next = b->next; free(b); b = next; }
    if (pending) *pending = call.pending;
    return call.result;
}

/* ---- aqua_call API ---- */

aqua_runtime *aqua_call_runtime(aqua_call *call) { return call->rt; }
uint32_t aqua_argc(aqua_call *call) { return call->count; }
aqua_value aqua_arg(aqua_call *call, uint32_t index) { return index < call->count ? call->arguments[index] : aqua_void(); }
double aqua_arg_number(aqua_call *call, uint32_t index) {
    aqua_value v = aqua_arg(call, index);
    return aqua_is_number(v) ? v.number : v.type == AQUA_BOOL ? v.number : 0;
}
int32_t aqua_arg_int(aqua_call *call, uint32_t index) {
    double n = aqua_arg_number(call, index);
    if (!(n >= -2147483648.0 && n <= 2147483647.0)) return n > 0 ? INT32_MAX : INT32_MIN;
    return (int32_t)n;
}
bool aqua_arg_bool(aqua_call *call, uint32_t index) { aqua_value v = aqua_arg(call, index); return v.type == AQUA_BOOL ? v.number != 0 : !aqua_is_nullish(v); }
bool aqua_has_arg(aqua_call *call, uint32_t index) { return index < call->count && !aqua_is_nullish(call->arguments[index]); }

void *aqua_scratch(aqua_call *call, size_t bytes) {
    bytes = (bytes + 15) & ~(size_t)15;
    scratch_block *b = call->scratch;
    if (!b || b->capacity - b->used < bytes) {
        size_t capacity = bytes > 4096 ? bytes : 4096;
        scratch_block *next = malloc(sizeof *next + capacity);
        next->next = b; next->used = 0; next->capacity = capacity;
        call->scratch = b = next;
    }
    void *p = b->data + b->used;
    b->used += bytes;
    return p;
}
aqua_str aqua_str_of(aqua_call *call, aqua_value text) {
    aqua_str s = {"", 0};
    if (text.type != AQUA_STRING && text.type != AQUA_ERROR) return s;
    size_t length;
    char *utf8 = guest_text_utf8(call->rt, text.ref, &length);
    char *copy = aqua_scratch(call, length + 1);
    memcpy(copy, utf8, length + 1);
    free(utf8);
    s.data = copy; s.length = length;
    return s;
}
aqua_str aqua_arg_str(aqua_call *call, uint32_t index) { return aqua_str_of(call, aqua_arg(call, index)); }

void *aqua_arg_object(aqua_call *call, uint32_t index, aqua_class *cls) {
    void *data = aqua_object_data(call->rt, aqua_arg(call, index), cls);
    if (!data && !call->failed) {
        char name[160], type[64];
        call_name(call->owner, call->member, name, sizeof name);
        aqua_fail(call, "%s: argument %u must be a %s object.", name, index + 1, cls ? names_first(cls->names, type, sizeof type) : "native");
    }
    return data;
}

void *aqua_self(aqua_call *call) {
    handle *h = handle_get(call->rt, call->self);
    return h ? h->data : NULL;
}
aqua_value aqua_self_value(aqua_call *call) { aqua_value v = {AQUA_NATIVE, call->self, 0}; return v; }
void *aqua_state(aqua_call *call) { return library_state(call->rt, call->owner->library); }
const char *aqua_call_script(aqua_call *call) {
    return call->context < call->rt->context_count ? call->rt->contexts[call->context].script : "";
}

void aqua_return(aqua_call *call, aqua_value result) { if (!call->failed) call->result = result; }
void aqua_return_int(aqua_call *call, int32_t n) { aqua_return(call, aqua_int(n)); }
void aqua_return_double(aqua_call *call, double n) { aqua_return(call, aqua_double(n)); }
void aqua_return_bool(aqua_call *call, bool b) { aqua_return(call, aqua_bool(b)); }
void aqua_return_null(aqua_call *call) { aqua_return(call, aqua_null()); }
void aqua_return_text(aqua_call *call, const char *utf8, size_t length) { if (!call->failed) call->result = aqua_text(call->rt, utf8, length); }
void aqua_return_cstring(aqua_call *call, const char *utf8) { aqua_return_text(call, utf8, strlen(utf8)); }

void aqua_vfail(aqua_call *call, const char *format, va_list arguments) {
    if (call->failed) return;
    char message[2048];
    vsnprintf(message, sizeof message, format, arguments);
    call->failed = true;
    call->result = aqua_cstring(call->rt, message);
    call->result.type = AQUA_ERROR;
}
void aqua_fail(aqua_call *call, const char *format, ...) {
    va_list arguments;
    va_start(arguments, format);
    aqua_vfail(call, format, arguments);
    va_end(arguments);
}
bool aqua_failed(aqua_call *call) { return call->failed; }
