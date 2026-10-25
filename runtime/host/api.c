/* The function table external native libraries receive (aqua/api.def lists its entries in order). */
#include "host.h"

const aqua_api_table host_api_table = {
    AQUA_API_VERSION,
#define AQUA_API(RETURN, NAME, PARAMETERS, ARGUMENTS) NAME,
#define AQUA_API_VOID(NAME, PARAMETERS, ARGUMENTS) NAME,
#include <aqua/api.def>
#undef AQUA_API
#undef AQUA_API_VOID
};
