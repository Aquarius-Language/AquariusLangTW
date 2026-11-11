/* The consumer of the services test library: default libraries without windows linked into a native library, and
   ONNX Runtime found beside it (scripts/test-exports.ps1). */
#include "services.h"
#include <stdio.h>

static void line(const char *name, char *text) {
    printf("%s %s\n", name, text ? text : svc_last_error());
    svc_free(text);
}

int main(void) {
    line("status", svc_status_text(404));
    line("json", svc_json("Ada", 3));
    printf("terminal %d\n", svc_terminal_supported());
    printf("onnx %d\n", svc_onnx_supported());
    if (svc_last_error()) printf("error %s\n", svc_last_error());
    svc_shutdown();
    return 0;
}
