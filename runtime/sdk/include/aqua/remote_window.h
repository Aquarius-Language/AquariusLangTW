/* aqua/remote_window.h — the remote window protocol, part of API version 4. A runtime started with
 * AQUARIUS_REMOTE_WINDOW=host:port shows every window it opens on a viewer elsewhere (a notebook output, another
 * machine's page) instead of the screen: aqua/window.h stays the same for every library, the runtime's desktop layer
 * sends what the window shows and receives the viewer's input. The `remotewindow` library is the viewer side.
 *
 * This header is the single definition of the wire format: the sender (runtime/platform/desktop/window_remote.c) and
 * the viewer (libraries/remotewindow) both use the helpers below, so they cannot drift apart. Changing the format
 * means raising AQUA_REMOTE_WINDOW_VERSION; a viewer refuses a stream of another version.
 *
 * Stream: TCP, the sender connects. Every message is  u32 length | u8 type | payload  (length counts type + payload,
 * little-endian integers, IEEE floats, strings as u32 byte count + UTF-8). Types below 64 go from the sender (the
 * application) to the viewer, the others back.
 *
 *   HELLO   u32 magic 'AQRW', u16 version, string token            first message; the viewer checks the token
 *   OPEN    u32 window, i32 width, i32 height, u32 flags, string title     (logical units, AQUA_WINDOW_* flags);
 *           the process's standard output up to the window's opening is written before it is sent
 *   CLOSE   u32 window
 *   TITLE   u32 window, string title
 *   SIZE    u32 window, i32 width, i32 height                   the application resized its window
 *   FRAME   u32 window, u32 number, i32 width, i32 height, f32 scale, u32 tile count, tiles
 *           tile: u16 x, u16 y, u16 width, u16 height, u8 encoding, u32 byte count, bytes
 *           The frame is width x height pixels (logical size x scale); its tiles are the regions that changed since
 *           the previous frame (every region after a size change). Encodings: RAW (top-down RGBA8), QOI (the Quite OK
 *           Image format, lossless, https://qoiformat.org).
 *   CURSOR  u32 window, i32 shape (GLFW standard cursor, 0 default, -1 hidden)
 *   TEXT    u32 window, u8 enabled, i32 x, i32 y, i32 width, i32 height   text input on/off and its caret box
 *   CLIPBOARD string text                                       the application set the clipboard
 *
 *   EVENT   u32 window, i32 type, i32 key, i32 scancode, i32 action, i32 mods, i32 button, f64 x, f64 y,
 *           u32 pointer, i32 pointer type, f32 pressure, f32 tilt x, f32 tilt y, i32 cursor, i32 index, i32 count,
 *           string text                                         an aqua_window_event (its time is the receiver's)
 *   VIEW    u32 window, i32 width, i32 height, f32 scale         the viewer's size (logical units) and pixel density;
 *                                                                also how a viewer attaches (again): the sender forgets
 *                                                                unacknowledged frames and sends the next one whole
 *   ACK     u32 window, u32 number                               the frame was shown; the sender keeps at most
 *                                                                AQUA_REMOTE_WINDOW_IN_FLIGHT frames unacknowledged
 *   PASTE   string text                                          the viewer's clipboard, for the next paste
 *
 * 遠端視窗通訊協定：以 AQUARIUS_REMOTE_WINDOW 啟動的執行環境把每個視窗的畫面送到別處的檢視器（筆記本的輸出、另一台
 * 電腦的網頁），並接收檢視器的輸入；所有函式庫照常使用 aqua/window.h。傳送端與檢視端共用本檔的編解碼函式。
 */
#ifndef AQUA_REMOTE_WINDOW_H
#define AQUA_REMOTE_WINDOW_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define AQUA_REMOTE_WINDOW_MAGIC 0x57525141u     /* "AQRW" read as little-endian */
#define AQUA_REMOTE_WINDOW_VERSION 1
#define AQUA_REMOTE_WINDOW_MAX_MESSAGE (64u << 20)
#define AQUA_REMOTE_WINDOW_TILE 64              /* the side of the squares frames are compared and sent in */
#define AQUA_REMOTE_WINDOW_IN_FLIGHT 2

enum {
    AQUA_REMOTE_HELLO = 1, AQUA_REMOTE_OPEN = 2, AQUA_REMOTE_CLOSE = 3, AQUA_REMOTE_TITLE = 4, AQUA_REMOTE_SIZE = 5,
    AQUA_REMOTE_FRAME = 6, AQUA_REMOTE_CURSOR = 7, AQUA_REMOTE_TEXT = 8, AQUA_REMOTE_CLIPBOARD = 9,
    AQUA_REMOTE_EVENT = 64, AQUA_REMOTE_VIEW = 65, AQUA_REMOTE_ACK = 66, AQUA_REMOTE_PASTE = 67,
};
enum { AQUA_REMOTE_RAW = 0, AQUA_REMOTE_QOI = 1 };

/* ---- Writing: a growing buffer of messages ---- */

typedef struct aqua_remote_buffer { uint8_t *data; size_t length, capacity; bool failed; } aqua_remote_buffer;

static inline bool aqua_remote_reserve(aqua_remote_buffer *b, size_t more) {
    if (b->failed) return false;
    if (b->length + more <= b->capacity) return true;
    size_t capacity = b->capacity ? b->capacity : 256;
    while (capacity < b->length + more) capacity *= 2;
    uint8_t *data = (uint8_t *)realloc(b->data, capacity);
    if (!data) { b->failed = true; return false; }
    b->data = data; b->capacity = capacity;
    return true;
}
static inline void aqua_remote_put(aqua_remote_buffer *b, const void *bytes, size_t count) {
    if (count && aqua_remote_reserve(b, count)) { memcpy(b->data + b->length, bytes, count); b->length += count; }
}
static inline void aqua_remote_u8(aqua_remote_buffer *b, uint8_t v) { aqua_remote_put(b, &v, 1); }
static inline void aqua_remote_u16(aqua_remote_buffer *b, uint16_t v) { uint8_t x[2] = {(uint8_t)v, (uint8_t)(v >> 8)}; aqua_remote_put(b, x, 2); }
static inline void aqua_remote_u32(aqua_remote_buffer *b, uint32_t v) {
    uint8_t x[4] = {(uint8_t)v, (uint8_t)(v >> 8), (uint8_t)(v >> 16), (uint8_t)(v >> 24)};
    aqua_remote_put(b, x, 4);
}
static inline void aqua_remote_i32(aqua_remote_buffer *b, int32_t v) { aqua_remote_u32(b, (uint32_t)v); }
static inline void aqua_remote_f32(aqua_remote_buffer *b, float v) { uint32_t u; memcpy(&u, &v, 4); aqua_remote_u32(b, u); }
static inline void aqua_remote_f64(aqua_remote_buffer *b, double v) {
    uint64_t u; memcpy(&u, &v, 8);
    aqua_remote_u32(b, (uint32_t)u); aqua_remote_u32(b, (uint32_t)(u >> 32));
}
static inline void aqua_remote_string(aqua_remote_buffer *b, const char *text) {
    size_t n = text ? strlen(text) : 0;
    aqua_remote_u32(b, (uint32_t)n);
    aqua_remote_put(b, text, n);
}
/* begin + payload + end frames one message: end writes the length into the place begin left for it. */
static inline size_t aqua_remote_begin(aqua_remote_buffer *b, uint8_t type) {
    size_t at = b->length;
    aqua_remote_u32(b, 0);
    aqua_remote_u8(b, type);
    return at;
}
static inline void aqua_remote_end(aqua_remote_buffer *b, size_t at) {
    if (b->failed) return;
    uint32_t n = (uint32_t)(b->length - at - 4);
    b->data[at] = (uint8_t)n; b->data[at + 1] = (uint8_t)(n >> 8); b->data[at + 2] = (uint8_t)(n >> 16); b->data[at + 3] = (uint8_t)(n >> 24);
}
static inline void aqua_remote_free(aqua_remote_buffer *b) { free(b->data); memset(b, 0, sizeof *b); }

/* ---- Reading: a cursor over one message's payload; reads past its end fail the reader and give zeros ---- */

typedef struct aqua_remote_reader { const uint8_t *at, *end; bool failed; } aqua_remote_reader;

static inline const uint8_t *aqua_remote_take(aqua_remote_reader *r, size_t count) {
    if (r->failed || (size_t)(r->end - r->at) < count) { r->failed = true; return NULL; }
    const uint8_t *p = r->at;
    r->at += count;
    return p;
}
static inline uint8_t aqua_remote_read_u8(aqua_remote_reader *r) { const uint8_t *p = aqua_remote_take(r, 1); return p ? p[0] : 0; }
static inline uint16_t aqua_remote_read_u16(aqua_remote_reader *r) { const uint8_t *p = aqua_remote_take(r, 2); return p ? (uint16_t)(p[0] | p[1] << 8) : 0; }
static inline uint32_t aqua_remote_read_u32(aqua_remote_reader *r) {
    const uint8_t *p = aqua_remote_take(r, 4);
    return p ? (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24 : 0;
}
static inline int32_t aqua_remote_read_i32(aqua_remote_reader *r) { return (int32_t)aqua_remote_read_u32(r); }
static inline float aqua_remote_read_f32(aqua_remote_reader *r) { uint32_t u = aqua_remote_read_u32(r); float v; memcpy(&v, &u, 4); return v; }
static inline double aqua_remote_read_f64(aqua_remote_reader *r) {
    uint64_t low = aqua_remote_read_u32(r), high = aqua_remote_read_u32(r), u = low | high << 32;
    double v; memcpy(&v, &u, 8);
    return v;
}
/* A string as a new NUL-terminated copy (free it), or NULL when the reader failed. */
static inline char *aqua_remote_read_string(aqua_remote_reader *r) {
    uint32_t n = aqua_remote_read_u32(r);
    const uint8_t *p = aqua_remote_take(r, n);
    if (!p) return NULL;
    char *text = (char *)malloc((size_t)n + 1);
    if (!text) { r->failed = true; return NULL; }
    memcpy(text, p, n);
    text[n] = 0;
    return text;
}

/* The next whole message in `bytes` (count bytes): its type and payload, and how many bytes it used; 0 when the message
   is still incomplete, -1 when the stream is malformed. */
static inline long aqua_remote_next(const uint8_t *bytes, size_t count, uint8_t *type, aqua_remote_reader *payload) {
    if (count < 5) return 0;
    uint32_t n = (uint32_t)bytes[0] | (uint32_t)bytes[1] << 8 | (uint32_t)bytes[2] << 16 | (uint32_t)bytes[3] << 24;
    if (n < 1 || n > AQUA_REMOTE_WINDOW_MAX_MESSAGE) return -1;
    if (count - 4 < n) return 0;
    *type = bytes[4];
    payload->at = bytes + 5; payload->end = bytes + 4 + n; payload->failed = false;
    return (long)n + 4;
}

/* ---- QOI: lossless RGBA images (https://qoiformat.org/qoi-specification.pdf) ---- */

static inline uint32_t aqua_qoi_hash(const uint8_t px[4]) { return (px[0] * 3u + px[1] * 5u + px[2] * 7u + px[3] * 11u) % 64u; }

/* Appends a QOI image of `width` x `height` RGBA pixels, rows `stride` bytes apart. */
static inline void aqua_qoi_encode(aqua_remote_buffer *b, const uint8_t *rgba, int32_t width, int32_t height, size_t stride) {
    static const uint8_t magic[4] = {'q', 'o', 'i', 'f'}, end[8] = {0, 0, 0, 0, 0, 0, 0, 1};
    aqua_remote_put(b, magic, 4);
    uint8_t size[10] = {(uint8_t)(width >> 24), (uint8_t)(width >> 16), (uint8_t)(width >> 8), (uint8_t)width,
                        (uint8_t)(height >> 24), (uint8_t)(height >> 16), (uint8_t)(height >> 8), (uint8_t)height, 4, 0};
    aqua_remote_put(b, size, 10);
    /* Worst case: 5 bytes a pixel. */
    if (!aqua_remote_reserve(b, (size_t)width * (size_t)height * 5 + 8)) return;
    uint8_t *out = b->data + b->length;
    uint8_t index[64][4];
    memset(index, 0, sizeof index);
    uint8_t previous[4] = {0, 0, 0, 255};
    uint32_t run = 0;
    for (int32_t y = 0; y < height; y++) {
        const uint8_t *row = rgba + (size_t)y * stride;
        for (int32_t x = 0; x < width; x++) {
            const uint8_t *px = row + (size_t)x * 4;
            bool last = y == height - 1 && x == width - 1;
            if (memcmp(px, previous, 4) == 0) {
                run++;
                if (run == 62 || last) { *out++ = (uint8_t)(0xc0 | (run - 1)); run = 0; }
                continue;
            }
            if (run) { *out++ = (uint8_t)(0xc0 | (run - 1)); run = 0; }
            uint32_t h = aqua_qoi_hash(px);
            if (memcmp(index[h], px, 4) == 0) *out++ = (uint8_t)h;
            else {
                memcpy(index[h], px, 4);
                if (px[3] == previous[3]) {
                    int dr = (int8_t)(uint8_t)(px[0] - previous[0]), dg = (int8_t)(uint8_t)(px[1] - previous[1]), db = (int8_t)(uint8_t)(px[2] - previous[2]);
                    int dr_dg = dr - dg, db_dg = db - dg;
                    if (dr > -3 && dr < 2 && dg > -3 && dg < 2 && db > -3 && db < 2)
                        *out++ = (uint8_t)(0x40 | (dr + 2) << 4 | (dg + 2) << 2 | (db + 2));
                    else if (dg > -33 && dg < 32 && dr_dg > -9 && dr_dg < 8 && db_dg > -9 && db_dg < 8) {
                        *out++ = (uint8_t)(0x80 | (dg + 32));
                        *out++ = (uint8_t)((dr_dg + 8) << 4 | (db_dg + 8));
                    } else { *out++ = 0xfe; *out++ = px[0]; *out++ = px[1]; *out++ = px[2]; }
                } else { *out++ = 0xff; memcpy(out, px, 4); out += 4; }
            }
            memcpy(previous, px, 4);
        }
    }
    memcpy(out, end, 8);
    out += 8;
    b->length = (size_t)(out - b->data);
}

/* Decodes a QOI image of exactly `width` x `height` pixels into `rgba` (rows `stride` bytes apart); false when the bytes
   are not one. */
static inline bool aqua_qoi_decode(const uint8_t *bytes, size_t count, uint8_t *rgba, int32_t width, int32_t height, size_t stride) {
    if (count < 22 || memcmp(bytes, "qoif", 4) != 0) return false;
    uint32_t w = (uint32_t)bytes[4] << 24 | (uint32_t)bytes[5] << 16 | (uint32_t)bytes[6] << 8 | bytes[7];
    uint32_t h = (uint32_t)bytes[8] << 24 | (uint32_t)bytes[9] << 16 | (uint32_t)bytes[10] << 8 | bytes[11];
    if (w != (uint32_t)width || h != (uint32_t)height) return false;
    const uint8_t *p = bytes + 14, *end = bytes + count - 8;
    uint8_t index[64][4];
    memset(index, 0, sizeof index);
    uint8_t px[4] = {0, 0, 0, 255};
    uint32_t run = 0;
    for (int32_t y = 0; y < height; y++) {
        uint8_t *row = rgba + (size_t)y * stride;
        for (int32_t x = 0; x < width; x++) {
            if (run) run--;
            else {
                if (p >= end) return false;
                uint8_t op = *p++;
                if (op == 0xfe) { if (end - p < 3) return false; px[0] = p[0]; px[1] = p[1]; px[2] = p[2]; p += 3; }
                else if (op == 0xff) { if (end - p < 4) return false; memcpy(px, p, 4); p += 4; }
                else switch (op >> 6) {
                case 0: memcpy(px, index[op], 4); break;
                case 1: px[0] = (uint8_t)(px[0] + ((op >> 4) & 3) - 2); px[1] = (uint8_t)(px[1] + ((op >> 2) & 3) - 2); px[2] = (uint8_t)(px[2] + (op & 3) - 2); break;
                case 2: {
                    if (p >= end) return false;
                    int dg = (op & 63) - 32, b2 = *p++;
                    px[0] = (uint8_t)(px[0] + dg - 8 + (b2 >> 4)); px[1] = (uint8_t)(px[1] + dg); px[2] = (uint8_t)(px[2] + dg - 8 + (b2 & 15));
                    break;
                }
                default: run = op & 63u; break;
                }
                memcpy(index[aqua_qoi_hash(px)], px, 4);
            }
            memcpy(row + (size_t)x * 4, px, 4);
        }
    }
    return true;
}

/* ---- Frames ---- */

/* Whether the w x h region at (x, y) differs between two frames `width` pixels wide. */
static inline bool aqua_remote_tile_changed(const uint8_t *a, const uint8_t *b, int32_t width, int32_t x, int32_t y, int32_t w, int32_t h) {
    for (int32_t row = y; row < y + h; row++) {
        size_t at = ((size_t)row * (size_t)width + (size_t)x) * 4;
        if (memcmp(a + at, b + at, (size_t)w * 4) != 0) return true;
    }
    return false;
}

/* Appends the FRAME message `number` of `window`: the QOI tiles of `rgba` (width x height, rows width * 4 bytes apart)
   that differ from `previous` (the frame sent before, the same size), or every tile when `previous` is NULL. Returns
   how many tiles it holds; with none (nothing changed) nothing is appended. */
static inline uint32_t aqua_remote_frame(aqua_remote_buffer *b, uint32_t window, uint32_t number, const uint8_t *rgba,
                                         int32_t width, int32_t height, float scale, const uint8_t *previous) {
    const int32_t T = AQUA_REMOTE_WINDOW_TILE;
    size_t at = aqua_remote_begin(b, AQUA_REMOTE_FRAME);
    aqua_remote_u32(b, window);
    aqua_remote_u32(b, number);
    aqua_remote_i32(b, width);
    aqua_remote_i32(b, height);
    aqua_remote_f32(b, scale);
    size_t count_at = b->length;
    aqua_remote_u32(b, 0);
    uint32_t tiles = 0;
    for (int32_t y = 0; y < height && !b->failed; y += T)
        for (int32_t x = 0; x < width && !b->failed; x += T) {
            int32_t w = width - x < T ? width - x : T, h = height - y < T ? height - y : T;
            if (previous && !aqua_remote_tile_changed(rgba, previous, width, x, y, w, h)) continue;
            aqua_remote_u16(b, (uint16_t)x);
            aqua_remote_u16(b, (uint16_t)y);
            aqua_remote_u16(b, (uint16_t)w);
            aqua_remote_u16(b, (uint16_t)h);
            aqua_remote_u8(b, AQUA_REMOTE_QOI);
            size_t size_at = b->length;
            aqua_remote_u32(b, 0);
            size_t start = b->length;
            aqua_qoi_encode(b, rgba + ((size_t)y * (size_t)width + (size_t)x) * 4, w, h, (size_t)width * 4);
            if (b->failed) break;
            uint32_t n = (uint32_t)(b->length - start);
            b->data[size_at] = (uint8_t)n; b->data[size_at + 1] = (uint8_t)(n >> 8);
            b->data[size_at + 2] = (uint8_t)(n >> 16); b->data[size_at + 3] = (uint8_t)(n >> 24);
            tiles++;
        }
    if (b->failed || !tiles) { if (!b->failed) b->length = at; return 0; }
    b->data[count_at] = (uint8_t)tiles; b->data[count_at + 1] = (uint8_t)(tiles >> 8);
    b->data[count_at + 2] = (uint8_t)(tiles >> 16); b->data[count_at + 3] = (uint8_t)(tiles >> 24);
    aqua_remote_end(b, at);
    return tiles;
}

/* Reads the next tile of a FRAME message into `frame` (frame_width x frame_height RGBA, tightly packed) and gives its
   region; false when the tile is malformed or outside the frame. */
static inline bool aqua_remote_read_tile(aqua_remote_reader *r, uint8_t *frame, int32_t frame_width, int32_t frame_height,
                                         int32_t *x, int32_t *y, int32_t *w, int32_t *h) {
    *x = aqua_remote_read_u16(r); *y = aqua_remote_read_u16(r); *w = aqua_remote_read_u16(r); *h = aqua_remote_read_u16(r);
    uint8_t encoding = aqua_remote_read_u8(r);
    uint32_t count = aqua_remote_read_u32(r);
    const uint8_t *bytes = aqua_remote_take(r, count);
    if (!bytes || *w <= 0 || *h <= 0 || *x + *w > frame_width || *y + *h > frame_height) return false;
    uint8_t *to = frame + ((size_t)*y * (size_t)frame_width + (size_t)*x) * 4;
    size_t stride = (size_t)frame_width * 4, row = (size_t)*w * 4;
    if (encoding == AQUA_REMOTE_QOI) return aqua_qoi_decode(bytes, count, to, *w, *h, stride);
    if (encoding != AQUA_REMOTE_RAW || count != row * (size_t)*h) return false;
    for (int32_t i = 0; i < *h; i++) memcpy(to + (size_t)i * stride, bytes + (size_t)i * row, row);
    return true;
}

#endif
