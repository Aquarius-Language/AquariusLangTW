/* Windows services of the native engine: the clock, BCrypt randomness and the trusted roots of the system's ROOT
 * certificate store (CertOpenSystemStore). */
#include "../native/native.h"
#include <windows.h>
#include <bcrypt.h>
#include <wincrypt.h>

int64_t net_clock_ms(void) { return (int64_t)GetTickCount64(); }

bool platform_random(void *buffer, size_t length) {
    return BCryptGenRandom(NULL, buffer, (ULONG)length, BCRYPT_USE_SYSTEM_PREFERRED_RNG) >= 0;
}

bool platform_trusted_roots(void (*add)(void *context, const uint8_t *der, size_t length), void *context) {
    HCERTSTORE store = CertOpenSystemStoreW(0, L"ROOT");
    if (!store) return false;
    PCCERT_CONTEXT certificate = NULL;
    while ((certificate = CertEnumCertificatesInStore(store, certificate)) != NULL)
        add(context, certificate->pbCertEncoded, certificate->cbCertEncoded);
    CertCloseStore(store, 0);
    return true;
}

bool platform_cleartext_permitted(const char *host) { (void)host; return true; }
void platform_thread_exit(void) {}
void platform_runtime_start(void) {}
