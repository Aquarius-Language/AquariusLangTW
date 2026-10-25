/* Extended grapheme cluster boundaries (Unicode UAX #29 rules GB3..GB13) over the property table in grapheme.def. */
#include "unicode.h"
#include <stdlib.h>

enum { OTHER, CONTROL, PREPEND, EXTEND, SPACING, HANGUL_L, HANGUL_V, HANGUL_T, REGIONAL, PICTOGRAPHIC,
       CR, LF, ZWJ, HANGUL_LV, HANGUL_LVT };

typedef struct range { uint32_t first, last; int property; } range;
static const range table[] = {
#define X(first, last, property) {first, last, property},
#include "grapheme.def"
#undef X
};
#define TABLE_SIZE (sizeof table / sizeof *table)

static int compare(const void *a, const void *b) {
    uint32_t x = ((const range *)a)->first, y = ((const range *)b)->first;
    return x < y ? -1 : x > y;
}

/* The table, sorted by code point once (the .def groups ranges by property). */
static const range *sorted(void) {
    static range *ordered;
    if (!ordered) {
        range *copy = malloc(sizeof table);
        for (size_t i = 0; i < TABLE_SIZE; i++) copy[i] = table[i];
        qsort(copy, TABLE_SIZE, sizeof *copy, compare);
        ordered = copy;
    }
    return ordered;
}

static int property(uint32_t c) {
    if (c == '\r') return CR;
    if (c == '\n') return LF;
    if (c == 0x200D) return ZWJ;
    if (c >= 0xAC00 && c <= 0xD7A3) return (c - 0xAC00) % 28 == 0 ? HANGUL_LV : HANGUL_LVT;
    const range *r = sorted();
    size_t lo = 0, hi = TABLE_SIZE;
    while (lo < hi) {
        size_t mid = (lo + hi) / 2;
        if (r[mid].last < c) lo = mid + 1;
        else if (r[mid].first > c) hi = mid;
        else return r[mid].property;
    }
    return OTHER;
}

typedef struct cursor {
    int previous;        /* property of the previous scalar, -1 before the first */
    bool emoji;          /* the previous scalars end in Extended_Pictographic Extend* */
    bool emoji_zwj;      /* ... followed by the previous scalar, a ZWJ */
    int regional;        /* regional indicators ending at the previous scalar */
} cursor;

/* Whether a cluster boundary precedes scalar `c`, then advances the cursor past it. */
static bool step(cursor *s, uint32_t c) {
    int p = property(c);
    bool pictographic = p == PICTOGRAPHIC;
    int q = s->previous;
    bool boundary;
    if (q < 0) boundary = true;
    else if (q == CR && p == LF) boundary = false;                                        /* GB3 */
    else if (q == CONTROL || q == CR || q == LF) boundary = true;                         /* GB4 */
    else if (p == CONTROL || p == CR || p == LF) boundary = true;                         /* GB5 */
    else if (q == HANGUL_L && (p == HANGUL_L || p == HANGUL_V || p == HANGUL_LV || p == HANGUL_LVT)) boundary = false; /* GB6 */
    else if ((q == HANGUL_LV || q == HANGUL_V) && (p == HANGUL_V || p == HANGUL_T)) boundary = false;              /* GB7 */
    else if ((q == HANGUL_LVT || q == HANGUL_T) && p == HANGUL_T) boundary = false;       /* GB8 */
    else if (p == EXTEND || p == ZWJ || p == SPACING) boundary = false;                   /* GB9, GB9a */
    else if (q == PREPEND) boundary = false;                                              /* GB9b */
    else if (q == ZWJ && pictographic && s->emoji_zwj) boundary = false;                  /* GB11 */
    else if (q == REGIONAL && p == REGIONAL && s->regional % 2 == 1) boundary = false;    /* GB12, GB13 */
    else boundary = true;                                                                 /* GB999 */
    bool emoji = pictographic || (p == EXTEND && s->emoji);
    s->emoji_zwj = p == ZWJ && s->emoji;
    s->emoji = emoji;
    s->regional = p == REGIONAL ? s->regional + 1 : 0;
    s->previous = p;
    return boundary;
}

size_t gfx_grapheme_last_start(const char *text, size_t length) {
    cursor s = {-1, false, false, 0};
    size_t last = 0;
    for (size_t i = 0; i < length;) {
        size_t start = i;
        uint32_t c = gfx_utf8_next(text, length, &i);
        if (step(&s, c)) last = start;
    }
    return last;
}

bool gfx_grapheme_boundary_utf16(const char *text, size_t length, size_t units) {
    cursor s = {-1, false, false, 0};
    size_t offset = 0;
    for (size_t i = 0; i < length;) {
        uint32_t c = gfx_utf8_next(text, length, &i);
        bool boundary = step(&s, c);
        if (offset == units) return boundary;
        offset += utf16_units(c);
        if (offset > units) return false; /* inside a surrogate pair */
    }
    return offset == units;
}
