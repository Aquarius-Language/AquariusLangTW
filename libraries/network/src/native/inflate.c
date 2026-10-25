/* Content-Encoding gzip and deflate (RFC 1952, 1950, 1951) for HTTP responses, decoded as bytes arrive so streamed
 * responses stay incremental. Input is kept from the start of the current deflate block: a block that is not
 * complete yet is decoded again once more bytes arrive (servers flushing a stream end a block per flush). Huffman
 * codes up to 9 bits decode through a lookup table, longer ones canonically. */
#include "native.h"
#include <stdlib.h>
#include <string.h>

#define WINDOW (32 * 1024)

typedef struct huffman {
    uint16_t fast[512];       /* 9-bit lookahead (bit-reversed): length << 9 | symbol, 0 when longer */
    uint16_t count[16];       /* codes per length */
    uint16_t symbol[320];     /* symbols ordered by code */
} huffman;

struct inflater {
    int format;               /* INFLATE_GZIP, INFLATE_DEFLATE (zlib or raw, detected) */
    enum { Z_HEADER, Z_BLOCKS, Z_TRAILER, Z_DONE } state;
    net_buffer input;         /* bytes from the current block's first byte */
    size_t bit;               /* bit offset of the current block inside input */
    net_buffer window;        /* recent output: back-references read it */
    size_t trailer;           /* bytes of trailer left */
};

static const uint16_t length_base[] = {3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258};
static const uint8_t length_extra[] = {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0};
static const uint16_t distance_base[] = {1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577};
static const uint8_t distance_extra[] = {0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13};

typedef struct bits { const uint8_t *data; size_t length, position; } bits;
static inline size_t available(const bits *b) { return b->length * 8 - b->position; }
static inline uint32_t peek(const bits *b, int n) {
    size_t byte = b->position >> 3;
    uint64_t v = 0;
    for (int i = 0; i < 5 && byte + (size_t)i < b->length; i++) v |= (uint64_t)b->data[byte + (size_t)i] << (8 * i);
    return (uint32_t)(v >> (b->position & 7)) & ((1u << n) - 1);
}
static inline bool take(bits *b, int n, uint32_t *v) {
    if (available(b) < (size_t)n) return false;
    *v = n ? peek(b, n) : 0;
    b->position += (size_t)n;
    return true;
}

enum { INFLATE_MORE = -1, INFLATE_BAD = -2 };

static bool build(huffman *h, const uint8_t *lengths, int n) {
    uint16_t offsets[16];
    memset(h, 0, sizeof *h);
    for (int i = 0; i < n; i++) h->count[lengths[i]]++;
    h->count[0] = 0;
    int left = 1;
    for (int len = 1; len < 16; len++) { left <<= 1; left -= h->count[len]; if (left < 0) return false; }
    offsets[1] = 0;
    for (int len = 1; len < 15; len++) offsets[len + 1] = (uint16_t)(offsets[len] + h->count[len]);
    for (int i = 0; i < n; i++) if (lengths[i]) h->symbol[offsets[lengths[i]]++] = (uint16_t)i;
    /* Fast table from canonical codes. */
    int code = 0;
    uint16_t next[16];
    for (int len = 1; len < 16; len++) { next[len] = (uint16_t)code; code = (code + h->count[len]) << 1; }
    for (int i = 0; i < n; i++) {
        int len = lengths[i];
        if (!len) continue;
        int c = next[len]++;
        if (len > 9) continue;
        int reversed = 0;
        for (int k = 0; k < len; k++) reversed |= ((c >> k) & 1) << (len - 1 - k);
        for (int fill = reversed; fill < 512; fill += 1 << len) h->fast[fill] = (uint16_t)(len << 9 | i);
    }
    return true;
}

static int decode(bits *b, const huffman *h) {
    size_t have = available(b);
    uint16_t entry = h->fast[peek(b, 9)];
    if (entry) {
        size_t len = entry >> 9;
        if (len > have) return INFLATE_MORE;
        b->position += len;
        return entry & 511;
    }
    int code = 0, first = 0, index = 0;
    for (int len = 1; len < 16; len++) {
        if (have < (size_t)len) return INFLATE_MORE;
        code |= (int)((peek(b, len) >> (len - 1)) & 1);
        int count = h->count[len];
        if (code - count < first) { b->position += (size_t)len; return h->symbol[index + (code - first)]; }
        index += count;
        first += count;
        first <<= 1;
        code <<= 1;
    }
    return INFLATE_BAD;
}

/* One block from b into the window; INFLATE_MORE when the block is not complete yet. */
static int block(bits *b, net_buffer *window, bool *final) {
    uint32_t v;
    if (!take(b, 1, &v)) return INFLATE_MORE;
    *final = v != 0;
    if (!take(b, 2, &v)) return INFLATE_MORE;
    if (v == 0) {
        b->position = (b->position + 7) & ~(size_t)7;
        uint32_t length, inverse;
        if (!take(b, 16, &length) || !take(b, 16, &inverse)) return INFLATE_MORE;
        if ((length ^ 0xFFFF) != inverse) return INFLATE_BAD;
        if (available(b) < (size_t)length * 8) return INFLATE_MORE;
        buffer_append(window, b->data + (b->position >> 3), length);
        b->position += (size_t)length * 8;
        return 0;
    }
    if (v == 3) return INFLATE_BAD;
    huffman literals, distances;
    uint8_t lengths[320];
    if (v == 1) {
        int i = 0;
        for (; i < 144; i++) lengths[i] = 8;
        for (; i < 256; i++) lengths[i] = 9;
        for (; i < 280; i++) lengths[i] = 7;
        for (; i < 288; i++) lengths[i] = 8;
        build(&literals, lengths, 288);
        for (i = 0; i < 30; i++) lengths[i] = 5;
        build(&distances, lengths, 30);
    } else {
        uint32_t hlit, hdist, hclen;
        if (!take(b, 5, &hlit) || !take(b, 5, &hdist) || !take(b, 4, &hclen)) return INFLATE_MORE;
        hlit += 257; hdist += 1; hclen += 4;
        static const uint8_t order[19] = {16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15};
        uint8_t code_lengths[19] = {0};
        for (uint32_t i = 0; i < hclen; i++) { if (!take(b, 3, &v)) return INFLATE_MORE; code_lengths[order[i]] = (uint8_t)v; }
        huffman codes;
        if (!build(&codes, code_lengths, 19)) return INFLATE_BAD;
        uint32_t n = 0;
        while (n < hlit + hdist) {
            int symbol = decode(b, &codes);
            if (symbol < 0) return symbol;
            if (symbol < 16) { lengths[n++] = (uint8_t)symbol; continue; }
            uint32_t repeat;
            uint8_t value = 0;
            if (symbol == 16) { if (!n) return INFLATE_BAD; value = lengths[n - 1]; if (!take(b, 2, &repeat)) return INFLATE_MORE; repeat += 3; }
            else if (symbol == 17) { if (!take(b, 3, &repeat)) return INFLATE_MORE; repeat += 3; }
            else { if (!take(b, 7, &repeat)) return INFLATE_MORE; repeat += 11; }
            if (n + repeat > hlit + hdist) return INFLATE_BAD;
            while (repeat--) lengths[n++] = value;
        }
        if (!build(&literals, lengths, (int)hlit)) return INFLATE_BAD;
        build(&distances, lengths + hlit, (int)hdist); /* incomplete distance codes are allowed */
    }
    for (;;) {
        int symbol = decode(b, &literals);
        if (symbol < 0) return symbol;
        if (symbol < 256) {
            if (window->length + 1 >= window->capacity && !buffer_reserve(window, 1)) return INFLATE_BAD;
            window->data[window->length++] = (uint8_t)symbol;
            continue;
        }
        if (symbol == 256) return 0;
        symbol -= 257;
        if (symbol >= 29) return INFLATE_BAD;
        uint32_t extra;
        if (!take(b, length_extra[symbol], &extra)) return INFLATE_MORE;
        size_t length = length_base[symbol] + extra;
        int d = decode(b, &distances);
        if (d < 0) return d;
        if (d >= 30) return INFLATE_BAD;
        if (!take(b, distance_extra[d], &extra)) return INFLATE_MORE;
        size_t distance = distance_base[d] + extra;
        if (distance > window->length) return INFLATE_BAD;
        if (!buffer_reserve(window, length)) return INFLATE_BAD;
        uint8_t *to = window->data + window->length, *from = to - distance;
        for (size_t i = 0; i < length; i++) to[i] = from[i];
        window->length += length;
    }
}

inflater *inflate_new(int format) {
    inflater *z = calloc(1, sizeof *z);
    z->format = format;
    return z;
}

void inflate_free(inflater *z) {
    if (!z) return;
    buffer_free(&z->input);
    buffer_free(&z->window);
    free(z);
}

/* The gzip or zlib header, or nothing (raw deflate); 0 when more bytes are needed. */
static long header_length(inflater *z, net_error *e) {
    const uint8_t *d = z->input.data;
    size_t n = z->input.length;
    if (z->format == INFLATE_GZIP) {
        if (n < 10) return 0;
        if (d[0] != 0x1F || d[1] != 0x8B || d[2] != 8) { net_error_set(e, NET_NETWORK, false, "Invalid gzip body / gzip 本文格式錯誤。"); return -1; }
        size_t at = 10;
        uint8_t flags = d[3];
        if (flags & 4) { if (n < at + 2) return 0; at += 2 + (size_t)(d[at] | d[at + 1] << 8); }
        for (int field = 8; field <= 16; field <<= 1) {
            if (!(flags & field)) continue;
            while (at < n && d[at]) at++;
            if (at >= n) return 0;
            at++;
        }
        if (flags & 2) at += 2;
        return at <= n ? (long)at : 0;
    }
    if (n < 2) return 0;
    if ((d[0] & 0x0F) == 8 && ((d[0] << 8) | d[1]) % 31 == 0) {
        if (d[1] & 0x20) { net_error_set(e, NET_UNSUPPORTED, false, "Unsupported deflate dictionary / 不支援的 deflate 字典。"); return -1; }
        return 2;
    }
    z->format = INFLATE_RAW;
    return -2; /* raw deflate: no header */
}

bool inflate_feed(inflater *z, const uint8_t *data, size_t length, net_buffer *out, net_error *e) {
    buffer_append(&z->input, data, length);
    for (;;) {
        if (z->state == Z_HEADER) {
            long n = header_length(z, e);
            if (n == -1) return false;
            if (n == 0) return true;
            if (n > 0) buffer_consume(&z->input, (size_t)n);
            z->bit = 0;
            z->state = Z_BLOCKS;
        }
        if (z->state == Z_BLOCKS) {
            bits b = {z->input.data, z->input.length, z->bit};
            size_t before = z->window.length;
            bool final = false;
            int r = block(&b, &z->window, &final);
            if (r == INFLATE_MORE) { z->window.length = before; return true; }
            if (r == INFLATE_BAD) return net_error_set(e, NET_NETWORK, false, "Invalid compressed body / 壓縮本文格式錯誤。");
            buffer_append(out, z->window.data + before, z->window.length - before);
            if (z->window.length > 2 * WINDOW) {
                memmove(z->window.data, z->window.data + z->window.length - WINDOW, WINDOW);
                z->window.length = WINDOW;
            }
            buffer_consume(&z->input, b.position >> 3);
            z->bit = b.position & 7;
            if (final) {
                if (z->bit) { buffer_consume(&z->input, 1); z->bit = 0; }
                z->state = Z_TRAILER;
                z->trailer = z->format == INFLATE_GZIP ? 8 : z->format == INFLATE_ZLIB_OR_RAW ? 4 : 0;
            }
            continue;
        }
        if (z->state == Z_TRAILER) {
            size_t n = z->input.length < z->trailer ? z->input.length : z->trailer;
            buffer_consume(&z->input, n);
            z->trailer -= n;
            if (z->trailer) return true;
            z->state = Z_DONE;
        }
        z->input.length = 0; /* anything after the stream is ignored */
        return true;
    }
}

bool inflate_end(inflater *z, net_error *e) {
    if (z->state == Z_DONE || (z->state == Z_TRAILER)) return true;
    if (z->state == Z_HEADER && z->input.length == 0) return true; /* an empty body */
    return net_error_set(e, NET_NETWORK, true, "The compressed body ended early / 壓縮本文提前結束。");
}
