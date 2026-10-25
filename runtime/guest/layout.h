/* Checks runtime.c's structs, value tags and status codes against layout.def, the layout every other reader of guest
   memory uses. Included by runtime.c after its type definitions. */
#ifndef AQUARIUS_RUNTIME_LAYOUT_H
#define AQUARIUS_RUNTIME_LAYOUT_H
#define AQUA_FIELD(STRUCT, FIELD, NAME, OFFSET) _Static_assert(offsetof(STRUCT, FIELD) == OFFSET, #STRUCT "." #FIELD " moved: update layout.def");
#define AQUA_SIZE(STRUCT, BYTES) _Static_assert(sizeof(STRUCT) == BYTES, #STRUCT " size changed: update layout.def");
#define AQUA_TAG(ENUM, NAME, VALUE) _Static_assert(ENUM == VALUE, "Value tag " #ENUM " changed: update layout.def");
#define AQUA_STATUS(NAME, VALUE)
#include "layout.def"
#undef AQUA_FIELD
#undef AQUA_SIZE
#undef AQUA_TAG
#undef AQUA_STATUS
#endif
