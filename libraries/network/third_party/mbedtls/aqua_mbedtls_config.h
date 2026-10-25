/* Mbed TLS configuration of the Network library (MBEDTLS_CONFIG_FILE): TLS 1.2 and 1.3 clients and servers with
 * X.509 certificates from the operating system's trust store, ECDHE key exchange, AES-GCM, ChaCha20-Poly1305 and
 * AES-CBC suites, RSA and ECDSA keys, PEM and PKCS#12 (.pfx) server identities, and thread-safe PSA crypto (worker
 * threads run handshakes concurrently). Everything else of Mbed TLS stays off. */
#ifndef AQUA_MBEDTLS_CONFIG_H
#define AQUA_MBEDTLS_CONFIG_H

/* Private names for this copy of Mbed TLS. */
#include "aqua_mbedtls_names.h"

/* Platform */
#define MBEDTLS_HAVE_ASM
#define MBEDTLS_HAVE_TIME
#define MBEDTLS_HAVE_TIME_DATE
#define MBEDTLS_THREADING_C
#define MBEDTLS_THREADING_PTHREAD
#define MBEDTLS_FS_IO
#define MBEDTLS_PLATFORM_C

/* Symmetric ciphers and hashes */
#define MBEDTLS_AES_C
#define MBEDTLS_AESNI_C
#define MBEDTLS_AESCE_C
#define MBEDTLS_GCM_C
#define MBEDTLS_CHACHA20_C
#define MBEDTLS_POLY1305_C
#define MBEDTLS_CHACHAPOLY_C
#define MBEDTLS_DES_C                 /* PKCS#12 files protected with 3DES */
#define MBEDTLS_CIPHER_C
#define MBEDTLS_CIPHER_MODE_CBC
#define MBEDTLS_CIPHER_PADDING_PKCS7
#define MBEDTLS_MD_C
#define MBEDTLS_SHA1_C
#define MBEDTLS_SHA224_C
#define MBEDTLS_SHA256_C
#define MBEDTLS_SHA384_C
#define MBEDTLS_SHA512_C

/* Randomness */
#define MBEDTLS_ENTROPY_C
#define MBEDTLS_CTR_DRBG_C
#define MBEDTLS_HMAC_DRBG_C
#define MBEDTLS_HKDF_C                /* TLS 1.3 key schedule */

/* Public keys */
#define MBEDTLS_BIGNUM_C
#define MBEDTLS_RSA_C
#define MBEDTLS_PKCS1_V15
#define MBEDTLS_PKCS1_V21
#define MBEDTLS_ECP_C
#define MBEDTLS_ECDH_C
#define MBEDTLS_ECDSA_C
#define MBEDTLS_ECDSA_DETERMINISTIC
#define MBEDTLS_ECP_NIST_OPTIM
#define MBEDTLS_ECP_DP_SECP256R1_ENABLED
#define MBEDTLS_ECP_DP_SECP384R1_ENABLED
#define MBEDTLS_ECP_DP_SECP521R1_ENABLED
#define MBEDTLS_ECP_DP_CURVE25519_ENABLED
#define MBEDTLS_PK_C
#define MBEDTLS_PK_PARSE_C
#define MBEDTLS_PK_WRITE_C
#define MBEDTLS_PSA_CRYPTO_C
#define MBEDTLS_PSA_KEY_STORE_DYNAMIC

/* Encodings and certificates */
#define MBEDTLS_ASN1_PARSE_C
#define MBEDTLS_ASN1_WRITE_C
#define MBEDTLS_BASE64_C
#define MBEDTLS_OID_C
#define MBEDTLS_PEM_PARSE_C
#define MBEDTLS_PKCS5_C
#define MBEDTLS_PKCS12_C
#define MBEDTLS_X509_USE_C
#define MBEDTLS_X509_CRT_PARSE_C
#define MBEDTLS_X509_RSASSA_PSS_SUPPORT

/* TLS */
#define MBEDTLS_SSL_TLS_C
#define MBEDTLS_SSL_CLI_C
#define MBEDTLS_SSL_SRV_C
#define MBEDTLS_SSL_PROTO_TLS1_2
#define MBEDTLS_SSL_PROTO_TLS1_3
#define MBEDTLS_SSL_TLS1_3_COMPATIBILITY_MODE
#define MBEDTLS_SSL_TLS1_3_KEY_EXCHANGE_MODE_EPHEMERAL_ENABLED
#define MBEDTLS_KEY_EXCHANGE_ECDHE_RSA_ENABLED
#define MBEDTLS_KEY_EXCHANGE_ECDHE_ECDSA_ENABLED
#define MBEDTLS_SSL_ENCRYPT_THEN_MAC
#define MBEDTLS_SSL_EXTENDED_MASTER_SECRET
#define MBEDTLS_SSL_SERVER_NAME_INDICATION
#define MBEDTLS_SSL_ALPN
#define MBEDTLS_SSL_KEEP_PEER_CERTIFICATE /* required by TLS 1.3 */

#endif
