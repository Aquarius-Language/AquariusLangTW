/* GIF through giflib 5.2.2: every frame is counted and checked against the limits; a decoded frame is the displayed
 * canvas (the frames before it composited with their disposal methods, as ImageMagick's coalesce does). Written with
 * an exact palette when the image has at most 256 colors (one index reserved for binary transparency), otherwise
 * quantized by giflib's median cut. */
#include "codecs.h"
#include <gif_lib.h>
#include <stdlib.h>
#include <string.h>

int GifQuantizeBuffer(unsigned int Width, unsigned int Height, int *ColorMapSize, const GifByteType *RedInput,
                      const GifByteType *GreenInput, const GifByteType *BlueInput, GifByteType *OutputBuffer,
                      GifColorType *OutputColorMap);

typedef struct reader { const uint8_t *data; size_t length, position; } reader;

static int read_bytes(GifFileType *gif, GifByteType *buffer, int count) {
    reader *r = gif->UserData;
    size_t n = (size_t)count;
    if (n > r->length - r->position) n = r->length - r->position;
    memcpy(buffer, r->data + r->position, n);
    r->position += n;
    return (int)n;
}

static bool gif_failure(app_error *e, int code) {
    const char *text = GifErrorString(code);
    return app_error_set(e, APP_INVALID_DATA, "Image codec failed: %s", text ? text : "corrupt GIF");
}

typedef struct frame_info { int left, top, width, height, disposal, transparent; bool interlaced; ColorMapObject *map; } frame_info;

/* Walks the records: counts frames within the limits and, when `target` >= 0, composites frames 0..target onto `canvas`
   (canvas_width x canvas_height RGBA, transparent initially). Returns the frame count or -1 on failure. */
static int walk(GifFileType *gif, int target, uint8_t *canvas, int canvas_width, int canvas_height, frame_info *first, frame_info *selected, app_error *e) {
    int frames = 0, disposal = 0, transparent = -1;
    int64_t pixels = 0;
    uint8_t *previous = NULL;
    GifPixelType *line = NULL;
    for (;;) {
        GifRecordType type;
        if (DGifGetRecordType(gif, &type) == GIF_ERROR) { gif_failure(e, gif->Error); frames = -1; break; }
        if (type == TERMINATE_RECORD_TYPE) break;
        if (type == EXTENSION_RECORD_TYPE) {
            int code;
            GifByteType *block;
            if (DGifGetExtension(gif, &code, &block) == GIF_ERROR) { gif_failure(e, gif->Error); frames = -1; break; }
            if (code == GRAPHICS_EXT_FUNC_CODE && block && block[0] >= 4) {
                GraphicsControlBlock gcb;
                if (DGifExtensionToGCB(block[0], block + 1, &gcb) == GIF_OK) { disposal = gcb.DisposalMode; transparent = gcb.TransparentColor; }
            }
            while (block) if (DGifGetExtensionNext(gif, &block) == GIF_ERROR) { gif_failure(e, gif->Error); frames = -1; goto done; }
            continue;
        }
        if (type != IMAGE_DESC_RECORD_TYPE) continue;
        if (DGifGetImageDesc(gif) == GIF_ERROR) { gif_failure(e, gif->Error); frames = -1; break; }
        frame_info info = {gif->Image.Left, gif->Image.Top, gif->Image.Width, gif->Image.Height, disposal, transparent,
                           gif->Image.Interlace, gif->Image.ColorMap ? gif->Image.ColorMap : gif->SColorMap};
        disposal = 0;
        transparent = -1;
        if (frames >= APP_MAX_FRAMES) { app_error_set(e, APP_LIMIT_EXCEEDED, "Image frame count exceeds the limit."); frames = -1; break; }
        int width = info.width > gif->SWidth ? info.width : gif->SWidth, height = info.height > gif->SHeight ? info.height : gif->SHeight;
        if (!app_check_pixels(width, height, 1, e)) { frames = -1; break; }
        pixels += (int64_t)width * height;
        if (pixels > APP_MAX_PIXELS) { app_error_set(e, APP_LIMIT_EXCEEDED, "Image frame allocation exceeds the limit."); frames = -1; break; }
        if (frames == 0 && first) *first = info;
        if (frames <= target) {
            if (frames == target && selected) *selected = info;
            if (info.disposal == 3) {
                free(previous);
                previous = malloc((size_t)canvas_width * canvas_height * 4);
                memcpy(previous, canvas, (size_t)canvas_width * canvas_height * 4);
            }
            line = realloc(line, (size_t)(info.width ? info.width : 1));
            static const int starts[] = {0, 4, 2, 1}, steps[] = {8, 8, 4, 2};
            int passes = info.interlaced ? 4 : 1;
            for (int pass = 0; pass < passes && frames >= 0; pass++) {
                for (int y = info.interlaced ? starts[pass] : 0; y < info.height; y += info.interlaced ? steps[pass] : 1) {
                    if (DGifGetLine(gif, line, info.width) == GIF_ERROR) { gif_failure(e, gif->Error); frames = -1; break; }
                    int cy = info.top + y;
                    if (cy < 0 || cy >= canvas_height) continue;
                    for (int x = 0; x < info.width; x++) {
                        int cx = info.left + x;
                        if (cx < 0 || cx >= canvas_width || line[x] == info.transparent) continue;
                        uint8_t *p = canvas + ((size_t)cy * canvas_width + cx) * 4;
                        if (info.map && line[x] < info.map->ColorCount) {
                            GifColorType c = info.map->Colors[line[x]];
                            p[0] = c.Red; p[1] = c.Green; p[2] = c.Blue;
                        } else p[0] = p[1] = p[2] = 0;
                        p[3] = 255;
                    }
                }
            }
            if (frames < 0) break;
            if (frames < target) {
                if (info.disposal == 2) {
                    for (int y = info.top; y < info.top + info.height && y < canvas_height; y++)
                        for (int x = info.left; x < info.left + info.width && x < canvas_width; x++)
                            if (y >= 0 && x >= 0) memset(canvas + ((size_t)y * canvas_width + x) * 4, 0, 4);
                } else if (info.disposal == 3 && previous) memcpy(canvas, previous, (size_t)canvas_width * canvas_height * 4);
            }
        } else {
            int size;
            GifByteType *code;
            if (DGifGetCode(gif, &size, &code) == GIF_ERROR) { gif_failure(e, gif->Error); frames = -1; break; }
            while (code) if (DGifGetCodeNext(gif, &code) == GIF_ERROR) { gif_failure(e, gif->Error); frames = -1; goto done; }
        }
        frames++;
    }
done:
    free(previous);
    free(line);
    if (frames == 0) { app_error_set(e, APP_INVALID_DATA, "Image codec failed: the GIF has no image."); frames = -1; }
    return frames;
}

static GifFileType *open_gif(reader *r, app_error *e) {
    int error = 0;
    GifFileType *gif = DGifOpen(r, read_bytes, &error);
    if (!gif) { gif_failure(e, error); return NULL; }
    if (!app_check_pixels(gif->SWidth ? gif->SWidth : 1, gif->SHeight ? gif->SHeight : 1, 1, e)) { DGifCloseFile(gif, &error); return NULL; }
    return gif;
}

static void describe(const frame_info *f, app_metadata *m, bool palette) {
    app_source_format(m, f->transparent >= 0 ? "PaletteAlpha" : "Palette", 8);
    if (palette && f->map && f->map->ColorCount > 0) {
        m->palette_count = (uint32_t)f->map->ColorCount;
        m->palette = malloc(m->palette_count * sizeof *m->palette);
        for (uint32_t i = 0; i < m->palette_count; i++) {
            GifColorType c = f->map->Colors[i];
            m->palette[i] = (uint32_t)c.Red << 24 | (uint32_t)c.Green << 16 | (uint32_t)c.Blue << 8 | ((int)i == f->transparent ? 0 : 255);
        }
    }
}

bool app_gif_inspect(const uint8_t *data, size_t length, app_image_info *info, app_error *e) {
    reader r = {data, length, 0};
    GifFileType *gif = open_gif(&r, e);
    if (!gif) return false;
    frame_info first = {0};
    int frames = walk(gif, -1, NULL, 0, 0, &first, NULL, e);
    if (frames > 0) {
        info->width = first.width > gif->SWidth ? first.width : gif->SWidth;
        info->height = first.height > gif->SHeight ? first.height : gif->SHeight;
        info->frames = frames;
        describe(&first, &info->metadata, false);
    }
    int error;
    DGifCloseFile(gif, &error);
    return frames > 0;
}

bool app_gif_decode(const uint8_t *data, size_t length, int32_t frame, app_picture *out, app_error *e) {
    app_image_info info;
    memset(&info, 0, sizeof info);
    if (!app_gif_inspect(data, length, &info, e) || app_frame_out_of_range(frame, info.frames, e)) return false;
    reader r = {data, length, 0};
    GifFileType *gif = open_gif(&r, e);
    if (!gif) return false;
    uint8_t *canvas = calloc((size_t)info.width * info.height, 4);
    frame_info selected = {0};
    int frames = walk(gif, frame, canvas, info.width, info.height, NULL, &selected, e);
    if (frames > 0) {
        out->width = info.width;
        out->height = info.height;
        out->rgba = canvas;
        describe(&selected, &out->metadata, true);
    } else free(canvas);
    int error;
    DGifCloseFile(gif, &error);
    return frames > 0;
}

static int write_bytes(GifFileType *gif, const GifByteType *data, int count) {
    app_bytes_append(gif->UserData, data, (size_t)count);
    return count;
}

bool app_gif_encode(const app_picture *p, const uint8_t *rgba, const app_encode_options *o, app_bytes *out, app_error *e) {
    (void)o;
    size_t count = (size_t)p->width * (size_t)p->height;
    bool transparent = false;
    for (size_t i = 0; i < count; i++) if (rgba[i * 4 + 3] == 0) { transparent = true; break; }
    int limit = transparent ? 255 : 256;
    GifColorType colors[256];
    int color_count = 0;
    uint8_t *indices = malloc(count ? count : 1);
    /* Exact palette when it fits. */
    bool exact = true;
    for (size_t i = 0; i < count && exact; i++) {
        const uint8_t *px = rgba + i * 4;
        if (px[3] == 0) continue;
        int k = 0;
        for (; k < color_count; k++) if (colors[k].Red == px[0] && colors[k].Green == px[1] && colors[k].Blue == px[2]) break;
        if (k == color_count) {
            if (color_count == limit) { exact = false; break; }
            colors[color_count++] = (GifColorType){px[0], px[1], px[2]};
        }
        indices[i] = (uint8_t)k;
    }
    if (!exact) {
        uint8_t *r = malloc(count), *g = malloc(count), *b = malloc(count);
        for (size_t i = 0; i < count; i++) { r[i] = rgba[i * 4]; g[i] = rgba[i * 4 + 1]; b[i] = rgba[i * 4 + 2]; }
        color_count = limit;
        int quantized = GifQuantizeBuffer((unsigned)p->width, (unsigned)p->height, &color_count, r, g, b, indices, colors);
        free(r); free(g); free(b);
        if (quantized != GIF_OK) { free(indices); return app_error_set(e, APP_UNAVAILABLE, "Image codec failed: palette quantization failed."); }
    }
    int transparent_index = -1;
    if (transparent) {
        transparent_index = color_count;
        colors[color_count++] = (GifColorType){0, 0, 0};
        for (size_t i = 0; i < count; i++) if (rgba[i * 4 + 3] == 0) indices[i] = (uint8_t)transparent_index;
    }
    int size = 2;
    while (size < color_count) size *= 2;
    for (int i = color_count; i < size; i++) colors[i] = (GifColorType){0, 0, 0};
    ColorMapObject *map = GifMakeMapObject(size, colors);
    int error = 0;
    GifFileType *gif = EGifOpen(out, write_bytes, &error);
    bool ok = gif && map;
    if (ok) {
        EGifSetGifVersion(gif, true);
        ok = EGifPutScreenDesc(gif, p->width, p->height, 8, 0, map) == GIF_OK;
        if (ok && transparent) {
            GraphicsControlBlock gcb = {DISPOSAL_UNSPECIFIED, false, 0, transparent_index};
            GifByteType extension[4];
            size_t length = EGifGCBToExtension(&gcb, extension);
            ok = EGifPutExtension(gif, GRAPHICS_EXT_FUNC_CODE, (int)length, extension) == GIF_OK;
        }
        if (ok) ok = EGifPutImageDesc(gif, 0, 0, p->width, p->height, false, NULL) == GIF_OK;
        for (int32_t y = 0; ok && y < p->height; y++) ok = EGifPutLine(gif, indices + (size_t)y * p->width, p->width) == GIF_OK;
        if (!ok) error = gif->Error;
        if (EGifCloseFile(gif, &error) != GIF_OK) ok = false;
    }
    if (map) GifFreeMapObject(map);
    free(indices);
    if (!ok) return gif_failure(e, error);
    return true;
}
