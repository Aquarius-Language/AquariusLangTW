/* The function table the runtime hands external native libraries: every function of aqua.h and window.h, in the
   order api.def lists them. Only the runtime (runtime/host/api.c) and aqua_api_stub.c include this. */
#ifndef AQUA_API_H
#define AQUA_API_H
#include "aqua.h"
#include "window.h"

typedef struct aqua_api_table {
    uint32_t version;
#define AQUA_API(RETURN, NAME, PARAMETERS, ARGUMENTS) RETURN (*NAME) PARAMETERS;
#define AQUA_API_VOID(NAME, PARAMETERS, ARGUMENTS) void (*NAME) PARAMETERS;
#include "api.def"
#undef AQUA_API
#undef AQUA_API_VOID
} aqua_api_table;
#endif
