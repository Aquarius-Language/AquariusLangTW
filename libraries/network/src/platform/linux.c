/* Linux services of the native engine: trusted roots from the distribution's CA bundle (Debian/Ubuntu, Fedora/RHEL,
 * openSUSE, Alpine), or every certificate file in /etc/ssl/certs. */
#include "../native/native.h"
#include <dirent.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The file's text with a terminating NUL counted in the length (PEM). */
static bool add_file(const char *path, void (*add)(void *, const uint8_t *, size_t), void *context) {
    FILE *f = fopen(path, "rb");
    if (!f) return false;
    net_buffer b = {0};
    uint8_t chunk[16384];
    size_t n;
    while ((n = fread(chunk, 1, sizeof chunk, f)) > 0) buffer_append(&b, chunk, n);
    fclose(f);
    bool ok = b.length > 0;
    if (ok) add(context, b.data, b.length + 1);
    buffer_free(&b);
    return ok;
}

bool platform_trusted_roots(void (*add)(void *context, const uint8_t *der, size_t length), void *context) {
    static const char *const bundles[] = {
        "/etc/ssl/certs/ca-certificates.crt", "/etc/pki/tls/certs/ca-bundle.crt", "/etc/ssl/ca-bundle.pem",
        "/etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem", "/etc/ssl/cert.pem",
    };
    for (size_t i = 0; i < sizeof bundles / sizeof bundles[0]; i++) if (add_file(bundles[i], add, context)) return true;
    DIR *directory = opendir("/etc/ssl/certs");
    if (!directory) return false;
    for (struct dirent *entry; (entry = readdir(directory)) != NULL;) {
        if (entry->d_name[0] == '.') continue;
        char path[512];
        snprintf(path, sizeof path, "/etc/ssl/certs/%s", entry->d_name);
        add_file(path, add, context);
    }
    closedir(directory);
    return true;
}

bool platform_cleartext_permitted(const char *host) { (void)host; return true; }
void platform_thread_exit(void) {}
void platform_runtime_start(void) {}
