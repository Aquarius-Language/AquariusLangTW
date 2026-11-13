/* Where a runtime error happened: the source line with the failing part underlined, and the call stack.

   When an execution fails, compiled code has left its failing instruction in the innermost frame (only on that error
   path: nothing is recorded while a program runs normally), each caller frame holds the continuation of the call it is
   in, and a native (fully typed) function that raised the error left its body and instruction in the execution.
   diagnostics_capture reads that chain before the execution is released. An error that ends an imported module, or a
   callback, and is raised again by the code that imported or called it (the same error value) continues the same
   call stack. diagnostics_report prints the error: the first line is exactly what format_value shows ("ERROR: ...", on
   standard output, which existing tools match on); the location, source line and call stack follow on standard error.
   The compiler's aquarius.debug section (WasmDebugInfo.cs) maps instructions to source ranges; it is read only here,
   after an error. Without that section (aqua build --no-debug-info) only the first line is printed. */
#include "host.h"
#include <stdio.h>
#include <stdlib.h>
#include <stdarg.h>
#include <string.h>

#define FRAME_IN_NATIVE 0xffffffffu  /* the frame's position is the execution's native error site */
#define FRAME_EXACT 0x80000000u      /* bit 31: an instruction index, else a continuation (block index) */
#define MAX_TRACE 4096

/* ---- The captured call stack ---------------------------------------------------------------------------------- */

static void trace_add(aqua_runtime *rt, uint32_t function, uint32_t position) {
    if (rt->trace_count == MAX_TRACE) return;
    if (!rt->trace) rt->trace = malloc(MAX_TRACE * sizeof *rt->trace);
    rt->trace[rt->trace_count].function = function;
    rt->trace[rt->trace_count].position = position;
    rt->trace_count++;
}

void diagnostics_capture(aqua_runtime *rt, uint32_t execution, aqua_value error) {
    if (error.type != AQUA_ERROR || !execution) return;
    /* A new error starts a new stack; the same error raised again by an importer or caller extends it outward. */
    if (rt->trace_error != error.ref || rt->trace_count == 0) { rt->trace_count = 0; rt->trace_error = error.ref; }
    uint32_t functions = guest_u32(rt, rt->app.program_address + G_Program_Functions);
    uint32_t native = guest_u32(rt, execution + G_Execution_ErrorFunction);
    if (native) trace_add(rt, native - 1, FRAME_EXACT | guest_u32(rt, execution + G_Execution_ErrorPc));
    bool first = true;
    for (uint32_t frame = guest_u32(rt, execution + G_Execution_Current), steps = 0; frame && steps < 100000; frame = guest_u32(rt, frame + G_Frame_Parent), steps++) {
        uint32_t function = (guest_u32(rt, frame + G_Frame_Function) - functions) / G_Function_Size;
        uint32_t position = guest_u32(rt, frame + G_Frame_Pc);
        bool innermost = first; first = false;
        if (position == FRAME_IN_NATIVE) continue;
        /* A deoptimized native function's own frame, when its native code raised the error: the native site says it. */
        if (innermost && native && native - 1 == function && !(position & FRAME_EXACT)) continue;
        trace_add(rt, function, position);
    }
}

/* ---- The debug section ---------------------------------------------------------------------------------------- */

typedef struct reader { const uint8_t *p, *end; bool failed; } reader;
static uint32_t u(reader *r) {
    uint32_t value = 0; int shift = 0;
    while (r->p < r->end && shift < 35) {
        uint8_t b = *r->p++;
        value |= (uint32_t)(b & 127) << shift; shift += 7;
        if (!(b & 128)) return value;
    }
    r->failed = true; return 0;
}
static const char *bytes_of(reader *r, uint32_t *length) {
    *length = u(r);
    if (r->failed || *length > (size_t)(r->end - r->p)) { r->failed = true; *length = 0; return ""; }
    const char *s = (const char *)r->p; r->p += *length; return s;
}

typedef struct debug_module { const char *path, *source; uint32_t path_length, source_length; } debug_module;
typedef struct debug_function { uint32_t module; const char *name; uint32_t name_length; const uint8_t *blocks, *spans; uint32_t block_count, span_count; } debug_function;
typedef struct debug_info { debug_module *modules; uint32_t module_count; debug_function *functions; uint32_t function_count; } debug_info;

static debug_info *debug_load(aqua_runtime *rt) {
    if (rt->debug) return rt->debug;
    debug_info *d = calloc(1, sizeof *d);
    rt->debug = d;
    if (!rt->app.debug_length) return d;
    reader r = {rt->app.bytes + rt->app.debug_offset, rt->app.bytes + rt->app.debug_offset + rt->app.debug_length, false};
    if (r.p >= r.end || *r.p++ != 1) return d; /* version 1 */
    uint32_t modules = u(&r);
    if (r.failed || modules > 100000) return d;
    d->modules = calloc(modules ? modules : 1, sizeof *d->modules);
    for (uint32_t m = 0; m < modules && !r.failed; m++) {
        d->modules[m].path = bytes_of(&r, &d->modules[m].path_length);
        d->modules[m].source = bytes_of(&r, &d->modules[m].source_length);
    }
    uint32_t functions = u(&r);
    if (r.failed || functions > 1000000) return d;
    d->functions = calloc(functions ? functions : 1, sizeof *d->functions);
    for (uint32_t f = 0; f < functions && !r.failed; f++) {
        debug_function *fn = &d->functions[f];
        fn->module = u(&r);
        fn->name = bytes_of(&r, &fn->name_length);
        fn->block_count = u(&r); fn->blocks = r.p;
        for (uint32_t b = 0; b < fn->block_count && !r.failed; b++) u(&r);
        fn->span_count = u(&r); fn->spans = r.p;
        for (uint32_t s = 0; s < 2 * fn->span_count && !r.failed; s++) u(&r);
    }
    if (r.failed) return d;
    d->module_count = modules; d->function_count = functions;
    return d;
}
void diagnostics_free(aqua_runtime *rt) {
    debug_info *d = rt->debug;
    if (d) { free(d->modules); free(d->functions); free(d); }
    free(rt->trace);
    rt->debug = NULL; rt->trace = NULL; rt->trace_count = 0;
}

/* The instruction a captured position names, or -1: an exact instruction, or the call before a continuation's block. */
static int64_t instruction_of(const debug_function *fn, uint32_t position) {
    if (position & FRAME_EXACT) return position & ~FRAME_EXACT;
    if (position >= fn->block_count) return -1;
    reader r = {fn->blocks, fn->spans, false};
    uint32_t start = 0;
    for (uint32_t b = 0; b <= position; b++) start = u(&r);
    return start > 0 ? (int64_t)start - 1 : -1;
}
/* The UTF-8 byte range of an instruction in its module's source; false when unknown. */
static bool span_of(const debug_function *fn, int64_t pc, uint32_t *start, uint32_t *length) {
    if (pc < 0 || pc >= fn->span_count) return false;
    reader r = {fn->spans, fn->spans + 10u * 2 * fn->span_count, false};
    uint32_t s = 0, l = 0;
    for (int64_t i = 0; i <= pc; i++) { s = u(&r); l = u(&r); }
    if (r.failed || s == 0) return false;
    *start = s - 1; *length = l;
    return true;
}

/* ---- Text: lines, columns and display widths ------------------------------------------------------------------ */

static uint32_t decode(const char *s, size_t length, size_t *i) {
    uint8_t c = (uint8_t)s[*i];
    int extra = c < 0x80 ? 0 : c < 0xe0 ? 1 : c < 0xf0 ? 2 : 3;
    uint32_t code = extra == 0 ? c : extra == 1 ? c & 0x1f : extra == 2 ? c & 0x0f : c & 0x07;
    (*i)++;
    for (int k = 0; k < extra && *i < length && ((uint8_t)s[*i] & 0xc0) == 0x80; k++) code = code << 6 | ((uint8_t)s[(*i)++] & 0x3f);
    return code;
}
/* Terminal columns of a character: 2 for East Asian wide and full-width ones (CJK, kana, Hangul, full-width forms,
   emoji), 0 for combining marks, else 1. */
static int char_width(uint32_t c) {
    if ((c >= 0x0300 && c <= 0x036f) || (c >= 0x200b && c <= 0x200f) || (c >= 0xfe00 && c <= 0xfe0f)) return 0;
    if ((c >= 0x1100 && c <= 0x115f) || (c >= 0x2e80 && c <= 0x303e) || (c >= 0x3041 && c <= 0x33ff) ||
        (c >= 0x3400 && c <= 0x4dbf) || (c >= 0x4e00 && c <= 0x9fff) || (c >= 0xa000 && c <= 0xa4cf) ||
        (c >= 0xac00 && c <= 0xd7a3) || (c >= 0xf900 && c <= 0xfaff) || (c >= 0xfe30 && c <= 0xfe4f) ||
        (c >= 0xff00 && c <= 0xff60) || (c >= 0xffe0 && c <= 0xffe6) || (c >= 0x1f300 && c <= 0x1f64f) ||
        (c >= 0x1f900 && c <= 0x1f9ff) || (c >= 0x20000 && c <= 0x3fffd)) return 2;
    return 1;
}
#define TAB_WIDTH 4

typedef struct location { uint32_t line, column, line_start, line_end; } location;
/* Line (1-based), column (1-based, in characters) and the line's byte range, of a byte offset into a source. */
static location locate(const char *source, uint32_t length, uint32_t offset) {
    location at = {1, 1, 0, 0};
    if (offset > length) offset = length;
    for (uint32_t i = 0; i < offset; i++) if (source[i] == '\n') { at.line++; at.line_start = i + 1; }
    for (size_t i = at.line_start; i < offset;) { decode(source, length, &i); at.column++; }
    at.line_end = at.line_start;
    while (at.line_end < length && source[at.line_end] != '\n') at.line_end++;
    if (at.line_end > at.line_start && source[at.line_end - 1] == '\r') at.line_end--;
    return at;
}

/* ---- Output --------------------------------------------------------------------------------------------------- */

typedef struct text { char *data; size_t length, capacity; } text;
static void put(text *t, const char *s, size_t n) {
    if (t->length + n + 1 > t->capacity) { t->capacity = (t->length + n + 1) * 2 + 64; t->data = realloc(t->data, t->capacity); }
    memcpy(t->data + t->length, s, n); t->length += n; t->data[t->length] = 0;
}
static void puts_(text *t, const char *s) { put(t, s, strlen(s)); }
static void putf(text *t, const char *format, ...) {
    char buffer[1024]; va_list a; va_start(a, format); int n = vsnprintf(buffer, sizeof buffer, format, a); va_end(a);
    if (n > 0) put(t, buffer, (size_t)n < sizeof buffer ? (size_t)n : sizeof buffer - 1);
}
static void repeat(text *t, char c, uint32_t n) { for (uint32_t i = 0; i < n; i++) put(t, &c, 1); }

/* Colours only on a terminal that shows them, never with NO_COLOR set (https://no-color.org); AQUARIUS_COLOR=1 forces
   them (and =0 turns them off). */
static bool colours(int stream) {
    const char *forced = platform_environment("AQUARIUS_COLOR");
    if (forced && forced[0]) return forced[0] != '0';
    const char *no = platform_environment("NO_COLOR");
    if (no && no[0]) return false;
    return platform_terminal_colors(stream);
}
typedef struct style { const char *error, *accent, *dim, *mark, *reset; } style;
static style style_for(bool on) {
    if (!on) return (style){"", "", "", "", ""};
    return (style){"\x1b[1;31m", "\x1b[1;34m", "\x1b[2m", "\x1b[1;31m", "\x1b[0m"};
}

/* The display columns of bytes [from, to) of a line, with tabs expanded from column `at`. */
static uint32_t columns(const char *s, size_t from, size_t to, uint32_t at) {
    uint32_t c = at;
    for (size_t i = from; i < to;) {
        if (s[i] == '\t') { c += TAB_WIDTH - c % TAB_WIDTH; i++; continue; }
        c += (uint32_t)char_width(decode(s, to, &i));
    }
    return c - at;
}
/* A line as displayed: tabs expanded to the same columns `columns` counts. */
static void put_line(text *t, const char *s, size_t from, size_t to) {
    uint32_t c = 0;
    for (size_t i = from; i < to;) {
        if (s[i] == '\t') { uint32_t n = TAB_WIDTH - c % TAB_WIDTH; repeat(t, ' ', n); c += n; i++; continue; }
        size_t before = i; c += (uint32_t)char_width(decode(s, to, &i));
        put(t, s + before, i - before);
    }
}

static bool is_module_body(aqua_runtime *rt, uint32_t function) {
    for (uint32_t i = 0; i < rt->app.module_count; i++) if (rt->app.modules[i].function == function) return true;
    return false;
}
static const debug_function *function_info(debug_info *d, uint32_t function) {
    return function < d->function_count ? &d->functions[function] : NULL;
}
static const debug_module *module_info(debug_info *d, const debug_function *fn) {
    return fn && fn->module > 0 && fn->module <= d->module_count ? &d->modules[fn->module - 1] : NULL;
}

/* The snippet: "  --> path:line:column", the line with its number, and ~~~ under the range (a range that goes on to
   later lines is underlined to the end of its first line and says where it ends). */
static void snippet(text *t, style st, const debug_module *m, uint32_t start, uint32_t length) {
    location at = locate(m->source, m->source_length, start);
    location end = locate(m->source, m->source_length, start + length);
    int gutter = snprintf(NULL, 0, "%u", at.line);
    putf(t, "%*s%s-->%s %.*s:%u:%u\n", gutter, "", st.accent, st.reset, (int)m->path_length, m->path, at.line, at.column);
    putf(t, "%*s %s|%s\n", gutter, "", st.accent, st.reset);
    putf(t, "%s%u |%s ", st.accent, at.line, st.reset);
    put_line(t, m->source, at.line_start, at.line_end);
    puts_(t, "\n");
    uint32_t to = end.line == at.line ? start + length : at.line_end;
    if (to > at.line_end) to = at.line_end;
    uint32_t lead = columns(m->source, at.line_start, start, 0);
    uint32_t width = start < to ? columns(m->source, start, to, lead) : 0;
    if (width == 0) width = 1;
    putf(t, "%*s %s|%s ", gutter, "", st.accent, st.reset);
    repeat(t, ' ', lead);
    puts_(t, st.mark); repeat(t, '~', width);
    if (end.line > at.line) putf(t, "...%s %s(continues to line %u)%s\n", st.reset, st.dim, end.line, st.reset);
    else putf(t, "%s\n", st.reset);
}

void diagnostics_report(aqua_runtime *rt, aqua_value result) {
    char *message = format_value(rt, result);
    bool out_colour = result.type == AQUA_ERROR && colours(1);
    style so = style_for(out_colour);
    /* "ERROR: " is the first line's own start (format_value); only its colour is added on a terminal. */
    if (out_colour && strncmp(message, "ERROR: ", 7) == 0) {
        platform_write(1, so.error, strlen(so.error)); platform_write(1, "ERROR:", 6); platform_write(1, so.reset, strlen(so.reset));
        platform_write(1, message + 6, strlen(message + 6));
    } else platform_write(1, message, strlen(message));
    platform_write(1, "\n", 1);
    free(message);
    if (result.type != AQUA_ERROR || rt->trace_error != result.ref || rt->trace_count == 0) return;
    debug_info *d = debug_load(rt);
    if (!d->function_count) return;
    style st = style_for(colours(2));
    text t = {0};
    bool shown = false;
    text previous = {0}; uint32_t repeated = 0;
    for (uint32_t i = 0; i <= rt->trace_count; i++) {
        text line = {0};
        if (i < rt->trace_count) {
            const debug_function *fn = function_info(d, rt->trace[i].function);
            const debug_module *m = module_info(d, fn);
            if (!fn) continue;
            int64_t pc = instruction_of(fn, rt->trace[i].position);
            uint32_t start = 0, length = 0;
            bool known = m && m->source_length && span_of(fn, pc, &start, &length);
            if (!shown && known) { snippet(&t, st, m, start, length); shown = true; }
            /* The call stack, innermost first: the function's name and file:line. */
            putf(&line, "  %sat%s ", st.dim, st.reset);
            if (fn->name_length) put(&line, fn->name, fn->name_length);
            else puts_(&line, is_module_body(rt, rt->trace[i].function) ? "<top level>" : "<anonymous>");
            if (m) {
                putf(&line, " %s(%.*s", st.dim, (int)m->path_length, m->path);
                if (known) putf(&line, ":%u", locate(m->source, m->source_length, start).line);
                putf(&line, ")%s", st.reset);
            } else putf(&line, " %s(built-in)%s", st.dim, st.reset);
            puts_(&line, "\n");
            /* Recursion: the same frame again is counted, not printed. */
            if (previous.length && line.length == previous.length && memcmp(line.data, previous.data, line.length) == 0) { repeated++; free(line.data); continue; }
        }
        if (repeated) putf(&t, "  %s... the frame above repeats %u more time%s%s\n", st.dim, repeated, repeated == 1 ? "" : "s", st.reset);
        repeated = 0;
        if (line.length) put(&t, line.data, line.length);
        free(previous.data); previous = line;
    }
    free(previous.data);
    if (rt->trace_count == MAX_TRACE) puts_(&t, "  ... (deeper frames are not shown)\n");
    if (t.length) platform_write(2, t.data, t.length);
    free(t.data);
}
