/* Linked into every external native library (aqua_add_library adds it): defines each aqua.h function as a call through
   the table the runtime passes to aqua_library_entry, so a library binds to no particular runtime executable and works
   inside renamed standalone applications. Web side modules import the functions by name instead and skip this file. */
#include <aqua/api.h>

static const aqua_api_table *aqua_table;

bool aqua_api_bind(aqua_library *library) {
    const aqua_api_table *table = *(const aqua_api_table *const *)library;
    if (!table || table->version < AQUA_API_VERSION) return false;
    aqua_table = table;
    return true;
}

#define AQUA_API(RETURN, NAME, PARAMETERS, ARGUMENTS) RETURN NAME PARAMETERS { return aqua_table->NAME ARGUMENTS; }
#define AQUA_API_VOID(NAME, PARAMETERS, ARGUMENTS) void NAME PARAMETERS { aqua_table->NAME ARGUMENTS; }
#include <aqua/api.def>
#undef AQUA_API
#undef AQUA_API_VOID

void aqua_fail(aqua_call *call, const char *format, ...) {
    va_list arguments;
    va_start(arguments, format);
    aqua_table->aqua_vfail(call, format, arguments);
    va_end(arguments);
}

void aqua_reject(aqua_pending *pending, const char *format, ...) {
    va_list arguments;
    va_start(arguments, format);
    aqua_table->aqua_vreject(pending, format, arguments);
    va_end(arguments);
}
