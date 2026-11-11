/* The POSIX process entry of the desktop launchers (linux, macos). Native library outputs (runtime/embed) link the rest
   of the layer without it. */
#include "../desktop/desktop.h"

int main(int count, char **arguments) {
    int code = desktop_main(count, arguments);
    platform_flush();
    return code;
}
