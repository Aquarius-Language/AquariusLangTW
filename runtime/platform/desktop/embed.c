/* The desktop layer's part of native library outputs (runtime/embed), in place of the launcher (main.c) and the window
   system: a library has no windows and loads no other native libraries yet, and writes its output after each call. */
#include "desktop.h"
#include "../../host/host.h"
#include "../../embed/aqua/embed.h"
#include <stdlib.h>
#include <string.h>

bool desktop_wait(double timeout_ms) { (void)timeout_ms; return false; }
void desktop_wake(void) {}
void platform_runtime_closing(aqua_runtime *rt) { (void)rt; }
const uint8_t *platform_asset(aqua_runtime *rt, const char *name, size_t *length) { (void)rt; (void)name; (void)length; return NULL; }
bool platform_load_library(aqua_runtime *rt, const char *name, char **error) {
    (void)rt;
    const char *text = "Native library outputs cannot load other native libraries yet: ";
    *error = malloc(strlen(text) + strlen(name) + 2);
    strcpy(*error, text); strcat(*error, name); strcat(*error, ".");
    return false;
}
void embed_platform_flush(void) { platform_flush(); }
