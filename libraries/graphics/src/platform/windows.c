/* Windows text through GDI, exactly as the earlier desktop measured and drew it: a font of em height ceil(size),
   antialiased, "sans-serif" as Segoe UI and "monospace" as Consolas; the box is GetTextExtentPoint32 plus 2 pixels of
   overhang and TextOut draws at its top-left. GDI links fonts for CJK where the system configures it. */
#include "platform.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

static wchar_t *wide(const char *text, size_t length, int *count) {
    int n = length ? MultiByteToWideChar(CP_UTF8, 0, text, (int)length, NULL, 0) : 0;
    wchar_t *w = malloc(((size_t)n + 1) * sizeof *w);
    if (n) MultiByteToWideChar(CP_UTF8, 0, text, (int)length, w, n);
    w[n] = 0;
    *count = n;
    return w;
}

static HFONT font_of(const char *name, float size) {
    const char *face = strcmp(name, "sans-serif") == 0 ? "Segoe UI" : strcmp(name, "monospace") == 0 ? "Consolas" : name;
    int count;
    wchar_t *w = wide(face, strlen(face), &count);
    HFONT font = CreateFontW(-(int)ceilf(size), 0, 0, 0, 400, 0, 0, 0, DEFAULT_CHARSET, 0, 0, ANTIALIASED_QUALITY, 0, w);
    free(w);
    return font;
}

bool gfx_platform_text_box(const char *name, float size, const char *text, size_t length, int *width, int *height) {
    int count;
    wchar_t *w = wide(text, length, &count);
    HDC dc = CreateCompatibleDC(NULL);
    HFONT font = font_of(name, size);
    HGDIOBJ old = SelectObject(dc, font);
    SIZE extent = {0, 0};
    BOOL ok = GetTextExtentPoint32W(dc, w, count, &extent);
    SelectObject(dc, old);
    DeleteObject(font);
    DeleteDC(dc);
    free(w);
    if (!ok) return false;
    *width = extent.cx + 2 > 1 ? extent.cx + 2 : 1;
    *height = extent.cy > 1 ? extent.cy : 1;
    return true;
}

bool gfx_platform_text_raster(const char *name, float size, const char *text, size_t length, int width, int height, uint8_t *alpha) {
    int count;
    wchar_t *w = wide(text, length, &count);
    HDC dc = CreateCompatibleDC(NULL);
    HFONT font = font_of(name, size);
    HGDIOBJ old_font = SelectObject(dc, font);
    BITMAPINFO info;
    memset(&info, 0, sizeof info);
    info.bmiHeader.biSize = sizeof info.bmiHeader;
    info.bmiHeader.biWidth = width;
    info.bmiHeader.biHeight = -height; /* top-down */
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    void *pixels = NULL;
    HBITMAP bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &pixels, NULL, 0);
    bool ok = bitmap && pixels;
    if (ok) {
        HGDIOBJ old_bitmap = SelectObject(dc, bitmap);
        memset(pixels, 0, (size_t)width * (size_t)height * 4);
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, RGB(255, 255, 255));
        ok = TextOutW(dc, 0, 0, w, count) != 0;
        GdiFlush();
        const uint8_t *bgra = pixels;
        for (size_t i = 0, n = (size_t)width * (size_t)height; i < n; i++) {
            uint8_t b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2];
            uint8_t m = r > g ? r : g;
            alpha[i] = m > b ? m : b;
        }
        SelectObject(dc, old_bitmap);
    }
    if (bitmap) DeleteObject(bitmap);
    SelectObject(dc, old_font);
    DeleteObject(font);
    DeleteDC(dc);
    free(w);
    return ok;
}

int gfx_platform_text_padding(void) { return 2; }

/* ---- Font enumeration ---- */

typedef struct enumeration { platform_font_add add; void *data; int count; } enumeration;

static int CALLBACK on_font(const LOGFONTW *font, const TEXTMETRICW *metrics, DWORD type, LPARAM user) {
    (void)metrics; (void)type;
    enumeration *e = (enumeration *)user;
    char family[LF_FACESIZE * 4];
    WideCharToMultiByte(CP_UTF8, 0, font->lfFaceName, -1, family, sizeof family, NULL, NULL);
    char style[32];
    strcpy(style, font->lfWeight >= 700 ? "Bold" : "Regular");
    if (font->lfItalic) strcat(style, " Italic");
    e->add(e->data, family, style);
    return ++e->count < 4096;
}

void gfx_platform_fonts(platform_font_add add, void (*done)(void *data, const char *error), void *data) {
    enumeration e = {add, data, 0};
    HDC dc = CreateCompatibleDC(NULL);
    LOGFONTW filter;
    memset(&filter, 0, sizeof filter);
    filter.lfCharSet = DEFAULT_CHARSET;
    EnumFontFamiliesExW(dc, &filter, on_font, (LPARAM)&e, 0);
    DeleteDC(dc);
    done(data, NULL);
}

static int CALLBACK on_match(const LOGFONTW *font, const TEXTMETRICW *metrics, DWORD type, LPARAM user) {
    (void)font; (void)metrics; (void)type;
    *(bool *)user = true;
    return 0;
}

bool gfx_platform_font_available(const char *family) {
    if (strcmp(family, "sans-serif") == 0 || strcmp(family, "monospace") == 0) return true;
    LOGFONTW filter;
    memset(&filter, 0, sizeof filter);
    filter.lfCharSet = DEFAULT_CHARSET;
    int count;
    wchar_t *w = wide(family, strlen(family), &count);
    if (count >= LF_FACESIZE) { free(w); return false; }
    wcscpy(filter.lfFaceName, w);
    free(w);
    bool found = false;
    HDC dc = CreateCompatibleDC(NULL);
    EnumFontFamiliesExW(dc, &filter, on_match, (LPARAM)&found, 0);
    DeleteDC(dc);
    return found;
}
