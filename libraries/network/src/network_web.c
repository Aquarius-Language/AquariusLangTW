/* The Network library on the web as one translation unit: every function is static, so the entry point
 * (aqua_library_network) is the only symbol of the side module. The browser moves the bytes (web/web.c). */
#include "network.c"
#include "values.c"
#include "url.c"
#include "client.c"
#include "websocket.c"
#include "web/web.c"
