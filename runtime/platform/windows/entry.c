/* The Windows process entry of the desktop launchers: the command line as UTF-8 arguments for desktop_main. Native
   library outputs (runtime/embed) link the rest of this layer without it. */
#define WIN32_LEAN_AND_MEAN
#define UNICODE
#include <windows.h>
#include <shellapi.h>
#include <stdlib.h>
#include "../desktop/desktop.h"

char *windows_utf8(const wchar_t *wide); /* platform.c */

int main(void) {
    SetConsoleOutputCP(CP_UTF8);
    int count = 0;
    wchar_t **wide = CommandLineToArgvW(GetCommandLineW(), &count);
    char **arguments = malloc((size_t)(count + 1) * sizeof *arguments);
    for (int i = 0; i < count; i++) arguments[i] = windows_utf8(wide[i]);
    arguments[count] = NULL;
    LocalFree(wide);
    int code = desktop_main(count, arguments);
    platform_flush();
    return code;
}
