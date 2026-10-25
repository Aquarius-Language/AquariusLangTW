/* Records: read-only objects with named fields (event.type, rectangle.width), as the earlier runtime returned for
 * events and measurements. A record wraps a string-keyed hash; records.def lists the fields records can have. */
#ifndef GRAPHICS_RECORDS_H
#define GRAPHICS_RECORDS_H

#include <aqua/aqua.h>

void gfx_records_register(aqua_library *library);
/* A record over `hash` (retained). */
aqua_value gfx_record_new(aqua_runtime *rt, aqua_value hash);
/* A new record from `count` name/value pairs. */
aqua_value gfx_record_of(aqua_runtime *rt, uint32_t count, const char *const *names, const aqua_value *values);

#endif
