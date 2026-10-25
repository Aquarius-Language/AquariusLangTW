/* Web side modules import aqua.h's functions by name from the host module, so there is no table to bind. */
#include <aqua/aqua.h>

bool aqua_api_bind(aqua_library *library) { (void)library; return true; }
