/* TLS through Mbed TLS: clients verify the peer against the operating system's trusted roots (platform_trusted_roots,
 * plus the file named by SSL_CERT_FILE when set, OpenSSL's convention) and the host name; servers present an identity
 * from a PKCS#12 (.pfx/.p12) file, as the old Kestrel server did, or from a PEM file. Sockets stay non-blocking: the
 * functions report TLS_WANT_READ/TLS_WANT_WRITE and socket.c waits. Also SHA-1, SHA-256 and Base64 for WebSocket
 * handshakes and ETags. */
#include "native.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "mbedtls/asn1.h"
#include "mbedtls/base64.h"
#include "mbedtls/cipher.h"
#include "mbedtls/error.h"
#include "mbedtls/md.h"
#include "mbedtls/oid.h"
#include "mbedtls/pk.h"
#include "mbedtls/pkcs12.h"
#include "mbedtls/pkcs5.h"
#include "mbedtls/psa_util.h"
#include "mbedtls/sha1.h"
#include "mbedtls/sha256.h"
#include "mbedtls/ssl.h"
#include "mbedtls/x509_crt.h"
#include "psa/crypto.h"
#if !defined(AQUA_OS_WINDOWS)
#include <errno.h>
#endif
#if defined(AQUA_OS_WINDOWS)
#define SEND_FLAGS 0
#else
#define SEND_FLAGS MSG_NOSIGNAL
#endif

struct tls_server {
    atomic_int refs;
    mbedtls_ssl_config config;
    mbedtls_x509_crt chain;
    mbedtls_pk_context key;
};
struct tls_session {
    mbedtls_ssl_context ssl;
    net_fd fd;
    tls_server *server;
};

/* ---- Shared client configuration, made once per process ---- */

static pthread_once_t prepared = PTHREAD_ONCE_INIT;
static bool ready;
static mbedtls_x509_crt roots;
static mbedtls_ssl_config http_client, raw_client;
static const char *http_alpn[] = {"http/1.1", NULL};

static void add_root(void *context, const uint8_t *data, size_t length) {
    (void)context;
    if (length && data[0] == 0x30) mbedtls_x509_crt_parse_der(&roots, data, length);
    else mbedtls_x509_crt_parse(&roots, data, length); /* PEM text, NUL included in `length` */
}

static bool client_config(mbedtls_ssl_config *c, const char **alpn) {
    mbedtls_ssl_config_init(c);
    if (mbedtls_ssl_config_defaults(c, MBEDTLS_SSL_IS_CLIENT, MBEDTLS_SSL_TRANSPORT_STREAM, MBEDTLS_SSL_PRESET_DEFAULT) != 0) return false;
    mbedtls_ssl_conf_authmode(c, MBEDTLS_SSL_VERIFY_REQUIRED);
    mbedtls_ssl_conf_ca_chain(c, &roots, NULL);
    mbedtls_ssl_conf_rng(c, mbedtls_psa_get_random, MBEDTLS_PSA_RANDOM_STATE);
    if (alpn) mbedtls_ssl_conf_alpn_protocols(c, alpn);
    return true;
}

static void prepare(void) {
    if (psa_crypto_init() != PSA_SUCCESS) return;
    mbedtls_x509_crt_init(&roots);
    platform_trusted_roots(add_root, NULL);
    const char *extra = getenv("SSL_CERT_FILE");
    if (extra && *extra) mbedtls_x509_crt_parse_file(&roots, extra);
    ready = client_config(&http_client, http_alpn) && client_config(&raw_client, NULL);
}

/* ---- Sessions ---- */

static int bio_send(void *context, const unsigned char *data, size_t length) {
    tls_session *s = context;
    long n = send(s->fd, (const char *)data, (int)(length > (1u << 30) ? (1u << 30) : length), SEND_FLAGS);
    if (n >= 0) return (int)n;
    return net_would_block(net_last_error()) ? MBEDTLS_ERR_SSL_WANT_WRITE : MBEDTLS_ERR_SSL_INTERNAL_ERROR;
}
static int bio_recv(void *context, unsigned char *buffer, size_t size) {
    tls_session *s = context;
    long n = recv(s->fd, (char *)buffer, (int)(size > (1u << 30) ? (1u << 30) : size), 0);
    if (n >= 0) return (int)n; /* 0: the peer closed */
    return net_would_block(net_last_error()) ? MBEDTLS_ERR_SSL_WANT_READ : MBEDTLS_ERR_SSL_INTERNAL_ERROR;
}

static tls_session *session_start(net_fd fd, const mbedtls_ssl_config *config, net_error *e) {
    tls_session *s = calloc(1, sizeof *s);
    s->fd = fd;
    mbedtls_ssl_init(&s->ssl);
    if (mbedtls_ssl_setup(&s->ssl, config) != 0) {
        mbedtls_ssl_free(&s->ssl);
        free(s);
        net_error_set(e, NET_TLS, false, MSG_TLS);
        return NULL;
    }
    mbedtls_ssl_set_bio(&s->ssl, s, bio_send, bio_recv, NULL);
    return s;
}

tls_session *tls_client_start(net_fd fd, const char *host, const char *alpn, net_error *e) {
    pthread_once(&prepared, prepare);
    if (!ready) { net_error_set(e, NET_TLS, false, MSG_TLS); return NULL; }
    tls_session *s = session_start(fd, alpn ? &http_client : &raw_client, e);
    if (s && mbedtls_ssl_set_hostname(&s->ssl, host) != 0) { tls_free(s); net_error_set(e, NET_TLS, false, MSG_TLS); return NULL; }
    return s;
}

tls_session *tls_server_start(net_fd fd, tls_server *server, net_error *e) {
    tls_session *s = session_start(fd, &server->config, e);
    if (s) { atomic_fetch_add(&server->refs, 1); s->server = server; }
    return s;
}

int tls_handshake(tls_session *s) {
    int r = mbedtls_ssl_handshake(&s->ssl);
    if (r == 0) return 0;
    if (r == MBEDTLS_ERR_SSL_WANT_READ) return TLS_WANT_READ;
    if (r == MBEDTLS_ERR_SSL_WANT_WRITE) return TLS_WANT_WRITE;
    return r == MBEDTLS_ERR_X509_CERT_VERIFY_FAILED ? TLS_VERIFY_FAILED : TLS_FAILED;
}

int tls_recv(tls_session *s, void *buffer, size_t size) {
    for (;;) {
        int r = mbedtls_ssl_read(&s->ssl, buffer, size);
        if (r > 0) return r;
        if (r == 0 || r == MBEDTLS_ERR_SSL_PEER_CLOSE_NOTIFY || r == MBEDTLS_ERR_SSL_CONN_EOF) return 0;
        if (r == MBEDTLS_ERR_SSL_WANT_READ) return TLS_WANT_READ;
        if (r == MBEDTLS_ERR_SSL_WANT_WRITE) return TLS_WANT_WRITE;
        if (r == MBEDTLS_ERR_SSL_RECEIVED_NEW_SESSION_TICKET) continue;
        return TLS_FAILED;
    }
}

int tls_send(tls_session *s, const void *data, size_t length) {
    int r = mbedtls_ssl_write(&s->ssl, data, length);
    if (r > 0) return r;
    if (r == MBEDTLS_ERR_SSL_WANT_READ) return TLS_WANT_READ;
    if (r == MBEDTLS_ERR_SSL_WANT_WRITE) return TLS_WANT_WRITE;
    return TLS_FAILED;
}

bool tls_pending(tls_session *s) { return mbedtls_ssl_get_bytes_avail(&s->ssl) > 0 || mbedtls_ssl_check_pending(&s->ssl); }
void tls_notify_close(tls_session *s) { mbedtls_ssl_close_notify(&s->ssl); }

void tls_free(tls_session *s) {
    if (!s) return;
    mbedtls_ssl_free(&s->ssl);
    if (s->server) tls_server_free(s->server);
    free(s);
}

/* ---- Server identities ---- */

static uint8_t *read_file(const char *path, size_t *length) {
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    net_buffer b = {0};
    uint8_t chunk[16384];
    size_t n;
    while ((n = fread(chunk, 1, sizeof chunk, f)) > 0) buffer_append(&b, chunk, n);
    fclose(f);
    if (!b.data) buffer_reserve(&b, 1);
    *length = b.length;
    return b.data; /* NUL-terminated by the buffer */
}

/* UTF-8 to a PKCS#12 BMPString: UTF-16 big-endian with a terminating 00 00. */
static uint8_t *bmp_password(const char *utf8, size_t *length) {
    size_t n = strlen(utf8);
    uint8_t *out = calloc(n * 4 + 2, 1);
    size_t at = 0;
    for (size_t i = 0; i < n;) {
        uint32_t c = (uint8_t)utf8[i];
        size_t extra = c >= 0xF0 ? 3 : c >= 0xE0 ? 2 : c >= 0xC0 ? 1 : 0;
        if (extra) c &= 0x3F >> extra;
        for (size_t k = 1; k <= extra && i + k < n; k++) c = c << 6 | ((uint8_t)utf8[i + k] & 0x3F);
        i += extra + 1;
        if (c >= 0x10000) {
            c -= 0x10000;
            uint32_t high = 0xD800 + (c >> 10), low = 0xDC00 + (c & 0x3FF);
            out[at++] = (uint8_t)(high >> 8); out[at++] = (uint8_t)high;
            out[at++] = (uint8_t)(low >> 8); out[at++] = (uint8_t)low;
        } else { out[at++] = (uint8_t)(c >> 8); out[at++] = (uint8_t)c; }
    }
    at += 2;
    *length = at;
    return out;
}

static bool oid_is(const mbedtls_asn1_buf *oid, const char *value, size_t length) {
    return oid->len == length && memcmp(oid->p, value, length) == 0;
}
#define OID_IS(oid, constant) oid_is(oid, constant, sizeof(constant) - 1)
#define OID_DATA MBEDTLS_OID_PKCS "\x07\x01"
#define OID_ENCRYPTED_DATA MBEDTLS_OID_PKCS "\x07\x06"
#define OID_KEY_BAG MBEDTLS_OID_PKCS12 "\x0a\x01\x01"
#define OID_SHROUDED_KEY_BAG MBEDTLS_OID_PKCS12 "\x0a\x01\x02"
#define OID_CERT_BAG MBEDTLS_OID_PKCS12 "\x0a\x01\x03"
#define OID_X509_CERTIFICATE MBEDTLS_OID_PKCS9 "\x16\x01"

/* Decrypts PBES2 (PBKDF2 with AES or 3DES) or PKCS#12 SHA-1 3DES content. */
static bool pfx_decrypt(const mbedtls_asn1_buf *algorithm, mbedtls_asn1_buf *parameters, const uint8_t *data, size_t length,
                        const char *password, net_buffer *out) {
    out->length = 0;
    if (!buffer_reserve(out, length + 16)) return false;
    size_t produced = 0;
    if (OID_IS(algorithm, MBEDTLS_OID_PKCS5_PBES2)) {
        if (mbedtls_pkcs5_pbes2_ext(parameters, MBEDTLS_PKCS5_DECRYPT, (const unsigned char *)password, strlen(password),
                                    data, length, out->data, out->capacity, &produced) != 0) return false;
        out->length = produced;
        return true;
    }
    bool three_key = OID_IS(algorithm, MBEDTLS_OID_PKCS12_PBE_SHA1_DES3_EDE_CBC);
    if (!three_key && !OID_IS(algorithm, MBEDTLS_OID_PKCS12_PBE_SHA1_DES2_EDE_CBC)) return false;
    /* pkcs-12PbeParams ::= SEQUENCE { salt OCTET STRING, iterations INTEGER } */
    unsigned char *p = parameters->p, *end = parameters->p + parameters->len;
    size_t salt_length;
    int iterations;
    if (mbedtls_asn1_get_tag(&p, end, &salt_length, MBEDTLS_ASN1_OCTET_STRING) != 0) return false;
    unsigned char *salt = p;
    p += salt_length;
    if (mbedtls_asn1_get_int(&p, end, &iterations) != 0) return false;
    size_t bmp_length;
    uint8_t *bmp = bmp_password(password, &bmp_length);
    unsigned char key[24], iv[8];
    size_t key_length = three_key ? 24 : 16;
    bool ok = mbedtls_pkcs12_derivation(key, key_length, bmp, bmp_length, salt, salt_length, MBEDTLS_MD_SHA1, MBEDTLS_PKCS12_DERIVE_KEY, iterations) == 0
        && mbedtls_pkcs12_derivation(iv, 8, bmp, bmp_length, salt, salt_length, MBEDTLS_MD_SHA1, MBEDTLS_PKCS12_DERIVE_IV, iterations) == 0;
    free(bmp);
    if (!ok) return false;
    mbedtls_cipher_context_t cipher;
    mbedtls_cipher_init(&cipher);
    const mbedtls_cipher_info_t *info = mbedtls_cipher_info_from_type(three_key ? MBEDTLS_CIPHER_DES_EDE3_CBC : MBEDTLS_CIPHER_DES_EDE_CBC);
    size_t finish = 0;
    ok = info && mbedtls_cipher_setup(&cipher, info) == 0
        && mbedtls_cipher_setkey(&cipher, key, (int)key_length * 8, MBEDTLS_DECRYPT) == 0
        && mbedtls_cipher_set_padding_mode(&cipher, MBEDTLS_PADDING_PKCS7) == 0
        && mbedtls_cipher_set_iv(&cipher, iv, 8) == 0 && mbedtls_cipher_reset(&cipher) == 0
        && mbedtls_cipher_update(&cipher, data, length, out->data, &produced) == 0
        && mbedtls_cipher_finish(&cipher, out->data + produced, &finish) == 0;
    mbedtls_cipher_free(&cipher);
    out->length = ok ? produced + finish : 0;
    return ok;
}

typedef struct pfx_contents { mbedtls_x509_crt *certificates[32]; int count; mbedtls_pk_context *key; } pfx_contents;

static bool parse_key(pfx_contents *c, const uint8_t *der, size_t length) {
    if (c->key) return true;
    mbedtls_pk_context *key = calloc(1, sizeof *key);
    mbedtls_pk_init(key);
    if (mbedtls_pk_parse_key(key, der, length, NULL, 0, mbedtls_psa_get_random, MBEDTLS_PSA_RANDOM_STATE) != 0) { mbedtls_pk_free(key); free(key); return false; }
    c->key = key;
    return true;
}

/* SafeContents ::= SEQUENCE OF SafeBag { bagId, [0] bagValue, bagAttributes } */
static bool parse_bags(pfx_contents *c, unsigned char *p, const unsigned char *end, const char *password) {
    size_t n;
    if (mbedtls_asn1_get_tag(&p, end, &n, MBEDTLS_ASN1_CONSTRUCTED | MBEDTLS_ASN1_SEQUENCE) != 0) return false;
    end = p + n;
    while (p < end) {
        if (mbedtls_asn1_get_tag(&p, end, &n, MBEDTLS_ASN1_CONSTRUCTED | MBEDTLS_ASN1_SEQUENCE) != 0) return false;
        unsigned char *bag_end = p + n;
        mbedtls_asn1_buf id = {MBEDTLS_ASN1_OID, 0, NULL};
        if (mbedtls_asn1_get_tag(&p, bag_end, &id.len, MBEDTLS_ASN1_OID) != 0) return false;
        id.p = p;
        p += id.len;
        if (mbedtls_asn1_get_tag(&p, bag_end, &n, MBEDTLS_ASN1_CONTEXT_SPECIFIC | MBEDTLS_ASN1_CONSTRUCTED | 0) != 0) return false;
        unsigned char *value = p, *value_end = p + n;
        if (OID_IS(&id, OID_KEY_BAG)) {
            if (!parse_key(c, value, n)) return false;
        } else if (OID_IS(&id, OID_SHROUDED_KEY_BAG)) {
            /* EncryptedPrivateKeyInfo ::= SEQUENCE { AlgorithmIdentifier, encryptedData OCTET STRING } */
            unsigned char *q = value;
            size_t m;
            mbedtls_asn1_buf algorithm, parameters;
            if (mbedtls_asn1_get_tag(&q, value_end, &m, MBEDTLS_ASN1_CONSTRUCTED | MBEDTLS_ASN1_SEQUENCE) != 0) return false;
            if (mbedtls_asn1_get_alg(&q, value_end, &algorithm, &parameters) != 0) return false;
            if (mbedtls_asn1_get_tag(&q, value_end, &m, MBEDTLS_ASN1_OCTET_STRING) != 0) return false;
            net_buffer plain = {0};
            bool ok = pfx_decrypt(&algorithm, &parameters, q, m, password, &plain) && parse_key(c, plain.data, plain.length);
            if (plain.data) mbedtls_platform_zeroize(plain.data, plain.capacity);
            buffer_free(&plain);
            if (!ok) return false;
        } else if (OID_IS(&id, OID_CERT_BAG)) {
            /* CertBag ::= SEQUENCE { certId, [0] certValue OCTET STRING } */
            unsigned char *q = value;
            size_t m;
            mbedtls_asn1_buf type = {MBEDTLS_ASN1_OID, 0, NULL};
            if (mbedtls_asn1_get_tag(&q, value_end, &m, MBEDTLS_ASN1_CONSTRUCTED | MBEDTLS_ASN1_SEQUENCE) != 0) return false;
            if (mbedtls_asn1_get_tag(&q, value_end, &type.len, MBEDTLS_ASN1_OID) != 0) return false;
            type.p = q;
            q += type.len;
            if (OID_IS(&type, OID_X509_CERTIFICATE) && c->count < 32) {
                if (mbedtls_asn1_get_tag(&q, value_end, &m, MBEDTLS_ASN1_CONTEXT_SPECIFIC | MBEDTLS_ASN1_CONSTRUCTED | 0) != 0) return false;
                if (mbedtls_asn1_get_tag(&q, value_end, &m, MBEDTLS_ASN1_OCTET_STRING) != 0) return false;
                mbedtls_x509_crt *certificate = calloc(1, sizeof *certificate);
                mbedtls_x509_crt_init(certificate);
                if (mbedtls_x509_crt_parse_der(certificate, q, m) != 0) { mbedtls_x509_crt_free(certificate); free(certificate); return false; }
                c->certificates[c->count++] = certificate;
            }
        }
        p = bag_end;
    }
    return true;
}

/* MacData ::= SEQUENCE { DigestInfo { AlgorithmIdentifier, digest }, macSalt, iterations }: HMAC over authSafe with a
   PKCS#12-derived key. Both the empty BMPString and no password count for an empty password, as in OpenSSL. */
static bool pfx_mac_valid(unsigned char *p, const unsigned char *end, const uint8_t *content, size_t length, const char *password) {
    size_t n, digest_length, salt_length;
    mbedtls_asn1_buf algorithm, parameters;
    if (mbedtls_asn1_get_tag(&p, end, &n, MBEDTLS_ASN1_CONSTRUCTED | MBEDTLS_ASN1_SEQUENCE) != 0) return false;
    if (mbedtls_asn1_get_tag(&p, end, &n, MBEDTLS_ASN1_CONSTRUCTED | MBEDTLS_ASN1_SEQUENCE) != 0) return false;
    if (mbedtls_asn1_get_alg(&p, end, &algorithm, &parameters) != 0) return false;
    if (mbedtls_asn1_get_tag(&p, end, &digest_length, MBEDTLS_ASN1_OCTET_STRING) != 0) return false;
    unsigned char *digest = p;
    p += digest_length;
    if (mbedtls_asn1_get_tag(&p, end, &salt_length, MBEDTLS_ASN1_OCTET_STRING) != 0) return false;
    unsigned char *salt = p;
    p += salt_length;
    int iterations = 1;
    if (p < end && mbedtls_asn1_get_int(&p, end, &iterations) != 0) return false;
    mbedtls_md_type_t md;
    if (mbedtls_oid_get_md_alg(&algorithm, &md) != 0) return false;
    const mbedtls_md_info_t *info = mbedtls_md_info_from_type(md);
    size_t size = mbedtls_md_get_size(info);
    if (!info || size != digest_length || size > 64) return false;
    size_t bmp_length;
    uint8_t *bmp = bmp_password(password, &bmp_length);
    bool valid = false;
    for (int attempt = 0; attempt < 2 && !valid; attempt++) {
        if (attempt == 1 && *password) break;
        unsigned char key[64], mac[64];
        size_t pwd_length = attempt == 0 ? bmp_length : 0;
        if (mbedtls_pkcs12_derivation(key, size, pwd_length ? bmp : NULL, pwd_length, salt, salt_length, md, MBEDTLS_PKCS12_DERIVE_MAC_KEY, iterations) != 0) continue;
        if (mbedtls_md_hmac(info, key, size, content, length, mac) != 0) continue;
        valid = memcmp(mac, digest, size) == 0;
    }
    free(bmp);
    return valid;
}

static bool pfx_load(uint8_t *der, size_t length, const char *password, pfx_contents *c, net_error *e) {
    unsigned char *p = der, *end = der + length;
    size_t n;
    int version;
    mbedtls_asn1_buf type = {MBEDTLS_ASN1_OID, 0, NULL};
#define REQUIRE(condition) do { if (!(condition)) return net_error_set(e, NET_INVALID, false, "Invalid PFX certificate / PFX 憑證無效。"); } while (0)
    REQUIRE(mbedtls_asn1_get_tag(&p, end, &n, MBEDTLS_ASN1_CONSTRUCTED | MBEDTLS_ASN1_SEQUENCE) == 0);
    end = p + n;
    REQUIRE(mbedtls_asn1_get_int(&p, end, &version) == 0 && version == 3);
    /* authSafe ContentInfo { data, [0] OCTET STRING } */
    REQUIRE(mbedtls_asn1_get_tag(&p, end, &n, MBEDTLS_ASN1_CONSTRUCTED | MBEDTLS_ASN1_SEQUENCE) == 0);
    REQUIRE(mbedtls_asn1_get_tag(&p, end, &type.len, MBEDTLS_ASN1_OID) == 0);
    type.p = p;
    p += type.len;
    REQUIRE(OID_IS(&type, OID_DATA));
    REQUIRE(mbedtls_asn1_get_tag(&p, end, &n, MBEDTLS_ASN1_CONTEXT_SPECIFIC | MBEDTLS_ASN1_CONSTRUCTED | 0) == 0);
    REQUIRE(mbedtls_asn1_get_tag(&p, end, &n, MBEDTLS_ASN1_OCTET_STRING) == 0);
    unsigned char *safe = p, *safe_end = p + n;
    p = safe_end;
    if (p < end && !pfx_mac_valid(p, end, safe, n, password))
        return net_error_set(e, NET_INVALID, false, "Invalid PFX password / PFX 密碼錯誤。");
    /* AuthenticatedSafe ::= SEQUENCE OF ContentInfo (data or encryptedData) */
    p = safe;
    REQUIRE(mbedtls_asn1_get_tag(&p, safe_end, &n, MBEDTLS_ASN1_CONSTRUCTED | MBEDTLS_ASN1_SEQUENCE) == 0);
    while (p < safe_end) {
        REQUIRE(mbedtls_asn1_get_tag(&p, safe_end, &n, MBEDTLS_ASN1_CONSTRUCTED | MBEDTLS_ASN1_SEQUENCE) == 0);
        unsigned char *info_end = p + n;
        REQUIRE(mbedtls_asn1_get_tag(&p, info_end, &type.len, MBEDTLS_ASN1_OID) == 0);
        type.p = p;
        p += type.len;
        REQUIRE(mbedtls_asn1_get_tag(&p, info_end, &n, MBEDTLS_ASN1_CONTEXT_SPECIFIC | MBEDTLS_ASN1_CONSTRUCTED | 0) == 0);
        if (OID_IS(&type, OID_DATA)) {
            REQUIRE(mbedtls_asn1_get_tag(&p, info_end, &n, MBEDTLS_ASN1_OCTET_STRING) == 0);
            REQUIRE(parse_bags(c, p, p + n, password));
        } else if (OID_IS(&type, OID_ENCRYPTED_DATA)) {
            /* EncryptedData { version, EncryptedContentInfo { contentType, algorithm, [0] IMPLICIT content } } */
            mbedtls_asn1_buf algorithm, parameters;
            REQUIRE(mbedtls_asn1_get_tag(&p, info_end, &n, MBEDTLS_ASN1_CONSTRUCTED | MBEDTLS_ASN1_SEQUENCE) == 0);
            REQUIRE(mbedtls_asn1_get_int(&p, info_end, &version) == 0);
            REQUIRE(mbedtls_asn1_get_tag(&p, info_end, &n, MBEDTLS_ASN1_CONSTRUCTED | MBEDTLS_ASN1_SEQUENCE) == 0);
            REQUIRE(mbedtls_asn1_get_tag(&p, info_end, &n, MBEDTLS_ASN1_OID) == 0);
            p += n;
            REQUIRE(mbedtls_asn1_get_alg(&p, info_end, &algorithm, &parameters) == 0);
            REQUIRE(mbedtls_asn1_get_tag(&p, info_end, &n, MBEDTLS_ASN1_CONTEXT_SPECIFIC | 0) == 0);
            net_buffer plain = {0};
            if (!pfx_decrypt(&algorithm, &parameters, p, n, password, &plain)) {
                buffer_free(&plain);
                return net_error_set(e, NET_UNSUPPORTED, false, "Unsupported PFX encryption or wrong password / 不支援的 PFX 加密方式或密碼錯誤。");
            }
            bool ok = parse_bags(c, plain.data, plain.data + plain.length, password);
            buffer_free(&plain);
            REQUIRE(ok);
        }
        p = info_end;
    }
#undef REQUIRE
    return true;
}

tls_server *tls_server_load(const char *path, const char *password, net_error *e) {
    pthread_once(&prepared, prepare);
    if (!ready) { net_error_set(e, NET_TLS, false, MSG_TLS); return NULL; }
    size_t length;
    uint8_t *data = read_file(path, &length);
    if (!data) { net_error_set(e, NET_INVALID, false, "Cannot read certificate / 無法讀取憑證：%s", path); return NULL; }
    tls_server *s = calloc(1, sizeof *s);
    atomic_init(&s->refs, 1);
    mbedtls_x509_crt_init(&s->chain);
    mbedtls_pk_init(&s->key);
    mbedtls_ssl_config_init(&s->config);
    bool ok;
    if (strstr((const char *)data, "-----BEGIN")) {
        /* PEM: the certificate chain (end entity first) and the private key in one file. */
        ok = mbedtls_x509_crt_parse(&s->chain, data, length + 1) == 0
            && mbedtls_pk_parse_key(&s->key, data, length + 1, (const unsigned char *)password, strlen(password), mbedtls_psa_get_random, MBEDTLS_PSA_RANDOM_STATE) == 0;
        if (!ok) net_error_set(e, NET_INVALID, false, "Invalid PEM certificate or key / PEM 憑證或金鑰無效。");
    } else {
        pfx_contents c = {{0}, 0, NULL};
        ok = pfx_load(data, length, password, &c, e);
        if (ok && (!c.key || !c.count)) ok = net_error_set(e, NET_INVALID, false, "The PFX holds no certificate with a private key / PFX 沒有含私密金鑰的憑證。");
        /* The end-entity certificate (the one matching the key) leads the chain. */
        int leaf = -1;
        for (int i = 0; ok && i < c.count && leaf < 0; i++)
            if (mbedtls_pk_check_pair(&c.certificates[i]->pk, c.key, mbedtls_psa_get_random, MBEDTLS_PSA_RANDOM_STATE) == 0) leaf = i;
        if (ok && leaf < 0) ok = net_error_set(e, NET_INVALID, false, "The PFX holds no certificate with a private key / PFX 沒有含私密金鑰的憑證。");
        for (int round = 0; ok && round < 2; round++)
            for (int i = 0; i < c.count; i++)
                if ((round == 0) == (i == leaf)) mbedtls_x509_crt_parse_der(&s->chain, c.certificates[i]->raw.p, c.certificates[i]->raw.len);
        if (c.key) { if (ok) s->key = *c.key; else mbedtls_pk_free(c.key); free(c.key); }
        for (int i = 0; i < c.count; i++) { mbedtls_x509_crt_free(c.certificates[i]); free(c.certificates[i]); }
    }
    mbedtls_platform_zeroize(data, length);
    free(data);
    if (ok) {
        ok = mbedtls_ssl_config_defaults(&s->config, MBEDTLS_SSL_IS_SERVER, MBEDTLS_SSL_TRANSPORT_STREAM, MBEDTLS_SSL_PRESET_DEFAULT) == 0
            && mbedtls_ssl_conf_own_cert(&s->config, &s->chain, &s->key) == 0;
        if (ok) {
            mbedtls_ssl_conf_rng(&s->config, mbedtls_psa_get_random, MBEDTLS_PSA_RANDOM_STATE);
            mbedtls_ssl_conf_alpn_protocols(&s->config, http_alpn);
        } else net_error_set(e, NET_TLS, false, "Cannot use the certificate / 無法使用憑證。");
    }
    if (!ok) { tls_server_free(s); return NULL; }
    return s;
}

void tls_server_free(tls_server *s) {
    if (!s || atomic_fetch_sub(&s->refs, 1) != 1) return;
    mbedtls_ssl_config_free(&s->config);
    mbedtls_x509_crt_free(&s->chain);
    mbedtls_pk_free(&s->key);
    free(s);
}

/* ---- Digests ---- */

void net_sha1(const void *data, size_t length, uint8_t digest[20]) { mbedtls_sha1(data, length, digest); }
void net_sha256(const void *data, size_t length, uint8_t digest[32]) { mbedtls_sha256(data, length, digest, 0); }
void net_base64(const uint8_t *data, size_t length, net_buffer *out) {
    size_t produced = 0;
    buffer_reserve(out, (length + 2) / 3 * 4 + 1);
    mbedtls_base64_encode(out->data + out->length, out->capacity - out->length, &produced, data, length);
    out->length += produced;
    out->data[out->length] = 0;
}
