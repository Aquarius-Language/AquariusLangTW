/* SHA-256 (FIPS 180-4), for verifying an executable's embedded application. */
#ifndef AQUA_SHA256_H
#define AQUA_SHA256_H
#include <stddef.h>
#include <stdint.h>
void sha256(const uint8_t *data, size_t length, uint8_t digest[32]);
#endif
