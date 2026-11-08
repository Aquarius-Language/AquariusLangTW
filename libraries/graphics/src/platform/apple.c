/* macOS and iOS text through Core Text: a CTLine lays the line out with the system's fonts and its fallback (CJK,
   Arabic, combining marks, emoji outlines), the box is the union of its typographic and glyph-path bounds, and Core
   Graphics draws it into an alpha-only bitmap. Like every platform's raster, the box starts at the pen's origin, so
   advances (where carets go) are measured from its left edge. */
#include "platform.h"
#include <CoreFoundation/CoreFoundation.h>
#include <CoreGraphics/CoreGraphics.h>
#include <CoreText/CoreText.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>

/* CSS weights (100..900) as Core Text's weight trait (-1..1), at the points Apple's system font uses. */
static double weight_trait(int weight) {
    static const double traits[] = {-0.8, -0.6, -0.4, 0.0, 0.23, 0.3, 0.4, 0.56, 0.62};
    double index = (weight < 100 ? 100 : weight > 900 ? 900 : weight) / 100.0 - 1.0;
    int low = (int)index;
    if (low >= 8) return traits[8];
    return traits[low] + (traits[low + 1] - traits[low]) * (index - low);
}

static CTFontRef font_create(const char *spec, float size) {
    char family[256];
    int weight;
    bool italic;
    gfx_font_parse(spec, family, sizeof family, &weight, &italic);
    CTFontRef base = NULL;
    if (strcmp(family, "sans-serif") == 0 || family[0] == 0) base = CTFontCreateUIFontForLanguage(kCTFontUIFontSystem, size, NULL);
    else if (strcmp(family, "monospace") == 0) base = CTFontCreateUIFontForLanguage(kCTFontUIFontUserFixedPitch, size, NULL);
    else {
        CFStringRef name = CFStringCreateWithCString(NULL, family, kCFStringEncodingUTF8);
        if (!name) return NULL;
        const void *keys[] = {kCTFontFamilyNameAttribute};
        const void *values[] = {name};
        CFDictionaryRef attributes = CFDictionaryCreate(NULL, keys, values, 1, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
        CTFontDescriptorRef descriptor = CTFontDescriptorCreateWithAttributes(attributes);
        base = CTFontCreateWithFontDescriptor(descriptor, size, NULL);
        CFRelease(descriptor); CFRelease(attributes); CFRelease(name);
    }
    if (!base || (weight == 400 && !italic)) return base;
    /* The weight and slant: the family's nearest face, as the other platforms' font matching picks it. */
    CFNumberRef weight_number = CFNumberCreate(NULL, kCFNumberDoubleType, &(double){weight_trait(weight)});
    CFNumberRef slant_number = CFNumberCreate(NULL, kCFNumberDoubleType, &(double){italic ? 1.0 : 0.0});
    const void *trait_keys[] = {kCTFontWeightTrait, kCTFontSlantTrait};
    const void *trait_values[] = {weight_number, slant_number};
    CFDictionaryRef traits = CFDictionaryCreate(NULL, trait_keys, trait_values, 2, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CFStringRef family_name = CTFontCopyFamilyName(base);
    const void *keys[] = {kCTFontFamilyNameAttribute, kCTFontTraitsAttribute};
    const void *values[] = {family_name, traits};
    CFDictionaryRef attributes = CFDictionaryCreate(NULL, keys, values, 2, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CTFontDescriptorRef descriptor = CTFontDescriptorCreateWithAttributes(attributes);
    CTFontRef styled = CTFontCreateWithFontDescriptor(descriptor, size, NULL);
    if (italic && styled && !(CTFontGetSymbolicTraits(styled) & kCTFontItalicTrait)) {
        CTFontRef slanted = CTFontCreateCopyWithSymbolicTraits(styled, size, NULL, kCTFontItalicTrait, kCTFontItalicTrait);
        if (slanted) { CFRelease(styled); styled = slanted; }
    }
    CFRelease(descriptor); CFRelease(attributes); CFRelease(family_name); CFRelease(traits);
    CFRelease(weight_number); CFRelease(slant_number);
    if (!styled) return base;
    CFRelease(base);
    return styled;
}

typedef struct layout { CTLineRef line; double advance, top, bottom, right; } layout;

static bool layout_make(layout *l, const char *spec, float size, const char *text, size_t length) {
    memset(l, 0, sizeof *l);
    if (length > 4096 * 4 || !(size > 0)) return false;
    CFStringRef string = CFStringCreateWithBytes(NULL, (const UInt8 *)text, (CFIndex)length, kCFStringEncodingUTF8, false);
    if (!string) return false;
    CTFontRef font = font_create(spec, size);
    if (!font) { CFRelease(string); return false; }
    const void *keys[] = {kCTFontAttributeName, kCTForegroundColorFromContextAttributeName};
    const void *values[] = {font, kCFBooleanTrue};
    CFDictionaryRef attributes = CFDictionaryCreate(NULL, keys, values, 2, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CFAttributedStringRef attributed = CFAttributedStringCreate(NULL, string, attributes);
    l->line = attributed ? CTLineCreateWithAttributedString(attributed) : NULL;
    if (attributed) CFRelease(attributed);
    CFRelease(attributes); CFRelease(font); CFRelease(string);
    if (!l->line) return false;
    CGFloat ascent = 0, descent = 0, leading = 0;
    l->advance = CTLineGetTypographicBounds(l->line, &ascent, &descent, &leading);
    CGRect ink = CTLineGetBoundsWithOptions(l->line, kCTLineBoundsUseGlyphPathBounds);
    /* Core Graphics' y grows upwards from the baseline. */
    l->top = fmax(ascent, CGRectGetMaxY(ink));
    l->bottom = fmax(descent, -CGRectGetMinY(ink));
    l->right = fmax(l->advance, CGRectGetMaxX(ink));
    return true;
}

static void box_of(const layout *l, int *width, int *height) {
    *width = (int)ceil(l->right) > 1 ? (int)ceil(l->right) : 1;
    *height = (int)ceil(l->top + l->bottom) > 1 ? (int)ceil(l->top + l->bottom) : 1;
}

bool gfx_platform_text_box(const char *font, float size, const char *text, size_t length, int *width, int *height) {
    layout l;
    if (!layout_make(&l, font, size, text, length)) return false;
    box_of(&l, width, height);
    CFRelease(l.line);
    return true;
}

bool gfx_platform_text_raster(const char *font, float size, const char *text, size_t length, int width, int height, int margin,
                              uint8_t *alpha) {
    layout l;
    if (!layout_make(&l, font, size, text, length)) return false;
    int full_width = width + 2 * margin, full_height = height + 2 * margin;
    /* An alpha-only bitmap whose memory rows run top-down, as the caller's mask does. */
    CGContextRef context = CGBitmapContextCreate(alpha, (size_t)full_width, (size_t)full_height, 8, (size_t)full_width, NULL,
                                                 (CGBitmapInfo)kCGImageAlphaOnly);
    if (!context) { CFRelease(l.line); return false; }
    CGContextSetGrayFillColor(context, 1, 1);
    CGContextSetShouldAntialias(context, true);
    CGContextSetTextPosition(context, margin, full_height - margin - l.top);
    CTLineDraw(l.line, context);
    CGContextFlush(context);
    CGContextRelease(context);
    CFRelease(l.line);
    return true;
}

bool gfx_platform_text_advance(const char *font, float size, const char *text, size_t length, double *advance) {
    layout l;
    if (!layout_make(&l, font, size, text, length)) return false;
    *advance = l.advance;
    CFRelease(l.line);
    return true;
}

static bool utf8_of(CFStringRef string, char *buffer, size_t size) {
    return string && CFStringGetCString(string, buffer, (CFIndex)size, kCFStringEncodingUTF8);
}

void gfx_platform_fonts(platform_font_add add, void (*done)(void *data, const char *error), void *data) {
    CTFontCollectionRef collection = CTFontCollectionCreateFromAvailableFonts(NULL);
    CFArrayRef descriptors = collection ? CTFontCollectionCreateMatchingFontDescriptors(collection) : NULL;
    if (!descriptors) {
        if (collection) CFRelease(collection);
        done(data, "Core Text listed no fonts.");
        return;
    }
    for (CFIndex i = 0, count = CFArrayGetCount(descriptors); i < count; i++) {
        CTFontDescriptorRef descriptor = CFArrayGetValueAtIndex(descriptors, i);
        CFStringRef family = CTFontDescriptorCopyAttribute(descriptor, kCTFontFamilyNameAttribute);
        CFStringRef style = CTFontDescriptorCopyAttribute(descriptor, kCTFontStyleNameAttribute);
        char family_text[256], style_text[256];
        if (utf8_of(family, family_text, sizeof family_text)) add(data, family_text, utf8_of(style, style_text, sizeof style_text) ? style_text : "Regular");
        if (family) CFRelease(family);
        if (style) CFRelease(style);
    }
    CFRelease(descriptors);
    CFRelease(collection);
    done(data, NULL);
}

typedef struct match { const char *family; bool found; } match;
static void compare(void *data, const char *family, const char *style) {
    (void)style;
    match *m = data;
    if (strcasecmp(m->family, family) == 0) m->found = true;
}
static void finished(void *data, const char *error) { (void)data; (void)error; }

bool gfx_platform_font_available(const char *family) {
    if (strcmp(family, "sans-serif") == 0 || strcmp(family, "monospace") == 0) return true;
    match m = {family, false};
    gfx_platform_fonts(compare, finished, &m);
    return m.found;
}
