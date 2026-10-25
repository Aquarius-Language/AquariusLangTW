/* PShader: WGSL shaders with the shared header (createShader, loadShader), canvas.shader()/resetShader(), user
   uniforms and named textures (loadTexture). */
#include "processing.h"
#include "../common/files.h"
#include "../modules/wgpu.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

void gfx_shader_dispose(pshader *s) {
    if (s->disposed) return;
    s->disposed = true;
    if (s->shader) gpu_shader_free(s->shader);
    s->shader = NULL;
    for (int i = 0; i < 8; i++) if (s->textures[i]) { aqua_release(s->owner->rt, s->textures[i]); s->textures[i] = 0; }
}

void gfx_shader_finalize(aqua_runtime *rt, void *data) {
    (void)rt;
    pshader *s = data;
    gfx_shader_dispose(s);
    processing *p = s->owner;
    if (s->previous) s->previous->next = s->next; else if (p->shaders == s) p->shaders = s->next;
    if (s->next) s->next->previous = s->previous;
    free(s);
}

pshader *gfx_shader_of_value(processing *p, aqua_value value) {
    pshader *s = aqua_object_data(p->rt, value, gfx_shader_class);
    return s && s->owner == p && !s->disposed ? s : NULL;
}

/* ---- Creation ---- */

typedef struct creation { processing *p; gpu_device *device; } creation;

static void created(gpu_shader *shader, const char *error, void *data) {
    gfx_async *a = data;
    creation *c = gfx_async_data(a);
    processing *p = c->p;
    if (error) { gfx_async_fail(a, "%s", error); return; }
    if (p->device != c->device) { gpu_shader_free(shader); gfx_async_fail(a, "Call Processing.size() first."); return; }
    pshader *s = calloc(1, sizeof *s);
    s->owner = p;
    s->shader = shader;
    s->next = p->shaders;
    if (p->shaders) p->shaders->previous = s;
    p->shaders = s;
    gfx_async_finish(a, aqua_object_new(gfx_async_runtime(a), gfx_shader_class, s));
}

static void create(aqua_call *call, processing *p, const char *vertex, const char *fragment) {
    gfx_async *a = gfx_async_begin(call, sizeof(creation));
    creation *c = gfx_async_data(a);
    c->p = p; c->device = p->device;
    gpu_shader_create(p->device, vertex, fragment, created, a);
    gfx_async_end(a);
}

static void p_createShader(aqua_call *call, processing *p, canvas *c) {
    (void)c;
    if (!gfx_require_sketch(call, p)) return;
    aqua_str vertex = gfx_text(call, 0), fragment = gfx_text(call, 1);
    if (!aqua_failed(call)) create(call, p, vertex.data, fragment.data);
}

static char *read_text(aqua_call *call, const char *path) {
    char error[1024];
    gfx_bytes file;
    if (!gfx_read_resource(aqua_call_runtime(call), path, &file, error, sizeof error)) { gfx_fail(call, "%s", error); return NULL; }
    size_t skip = file.length >= 3 && file.data[0] == 0xEF && file.data[1] == 0xBB && file.data[2] == 0xBF ? 3 : 0;
    char *text = malloc(file.length - skip + 1);
    memcpy(text, file.data + skip, file.length - skip);
    text[file.length - skip] = 0;
    gfx_bytes_free(&file);
    return text;
}

static void p_loadShader(aqua_call *call, processing *p, canvas *c) {
    (void)c;
    if (!gfx_require_sketch(call, p)) return;
    aqua_str fragment_path = gfx_text(call, 0);
    aqua_str vertex_path = aqua_argc(call) == 2 ? gfx_text(call, 1) : (aqua_str){"", 0};
    if (aqua_failed(call)) return;
    char *vertex = aqua_argc(call) == 2 ? read_text(call, vertex_path.data) : NULL;
    if (aqua_argc(call) == 2 && !vertex) return;
    char *fragment = read_text(call, fragment_path.data);
    if (fragment) create(call, p, vertex ? vertex : gpu_wgsl_vertex, fragment);
    free(vertex);
    free(fragment);
}

static void p_shader(aqua_call *call, processing *p, canvas *c) {
    if (!gfx_canvas_live(call, c) || !gfx_require_sketch(call, p)) return;
    pshader *s = gfx_shader_of_value(p, aqua_arg(call, 0));
    if (!s) { gfx_fail(call, "Expected a shader from this runtime."); return; }
    if (c->custom_ref) aqua_release(p->rt, c->custom_ref);
    c->custom_ref = aqua_retain(p->rt, aqua_arg(call, 0));
    c->custom = s;
}

static void p_resetShader(aqua_call *call, processing *p, canvas *c) {
    if (!gfx_canvas_live(call, c)) return;
    if (c->custom_ref) aqua_release(p->rt, c->custom_ref);
    c->custom_ref = 0;
    c->custom = NULL;
}

static void p_loadTexture(aqua_call *call, processing *p, canvas *c) {
    (void)c;
    if (!gfx_require_sketch(call, p)) return;
    aqua_value texture;
    if (!gfx_texture_load(call, p->device, 0, aqua_null(), &texture)) return;
    GFX_PUSH(p->textures, p->texture_count, p->texture_capacity, aqua_retain(p->rt, texture));
    aqua_return(call, texture);
}

/* ---- PShader members ---- */

static bool live(aqua_call *call, processing *p, pshader *s) {
    if (!gfx_require_sketch(call, p)) return false;
    if (s->disposed || !s->shader) { gfx_fail(call, "Shader is disposed."); return false; }
    return true;
}

static void p_shader_set(aqua_call *call, processing *p, pshader *s) {
    if (!live(call, p, s)) return;
    aqua_str name = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    float values[16];
    int count = 0;
    uint32_t n = aqua_argc(call);
    if (n == 2 && aqua_arg(call, 1).type == AQUA_ARRAY) {
        uint32_t length;
        float *floats = gfx_floats(call, aqua_arg(call, 1), &length);
        if (!floats) return;
        if (length > 16) { gfx_fail(call, "Uniform %s needs at most 16 values.", name.data); return; }
        memcpy(values, floats, length * sizeof *values);
        count = (int)length;
    } else {
        for (uint32_t i = 1; i < n; i++) values[count++] = gfx_float(call, i);
        if (aqua_failed(call)) return;
    }
    char error[256];
    if (!gpu_shader_set(s->shader, name.data, values, count, error, sizeof error)) gfx_fail(call, "%s", error);
}

static void p_shader_setInt(aqua_call *call, processing *p, pshader *s) {
    if (!live(call, p, s)) return;
    aqua_str name = gfx_text(call, 0);
    int value = gfx_int(call, 1);
    if (aqua_failed(call)) return;
    char error[256];
    if (!gpu_shader_set_int(s->shader, name.data, value, error, sizeof error)) gfx_fail(call, "%s", error);
}

static void p_shader_setTexture(aqua_call *call, processing *p, pshader *s) {
    if (!gfx_require_sketch(call, p)) return;
    aqua_str name = gfx_text(call, 0);
    if (aqua_failed(call)) return;
    gpu_texture *texture = gfx_texture_object_texture(p->rt, aqua_arg(call, 1));
    bool ours = false;
    for (size_t i = 0; texture && i < p->texture_count; i++) if (aqua_equal(p->rt, aqua_ref_value(p->rt, p->textures[i]), aqua_arg(call, 1))) ours = true;
    if (!texture || !ours) { gfx_fail(call, "Expected a texture from this runtime."); return; }
    if (s->disposed || !s->shader) { gfx_fail(call, "Shader or texture is disposed."); return; }
    char error[256];
    if (!gpu_shader_set_texture(s->shader, name.data, texture, error, sizeof error)) { gfx_fail(call, "%s", error); return; }
    /* Keep the texture object alive while the shader samples it (one slot per name). */
    int slot = -1;
    for (int i = 0; i < 8; i++) if (s->textures[i] && strcmp(s->texture_names[i], name.data) == 0) slot = i;
    for (int i = 0; slot < 0 && i < 8; i++) if (!s->textures[i]) slot = i;
    if (slot < 0) return;
    if (s->textures[slot]) aqua_release(p->rt, s->textures[slot]);
    s->textures[slot] = aqua_retain(p->rt, aqua_arg(call, 1));
    snprintf(s->texture_names[slot], sizeof s->texture_names[slot], "%s", name.data);
}

#define SHADER_MEMBERS(X) X(shader_set, "set|設定", 2, 5) X(shader_setInt, "setInt|設定整數", 2, 2) X(shader_setTexture, "setTexture|設定紋理", 2, 2)
#define SHADER_WRAPPER(name, names, minimum, maximum) OBJECT_WRAPPER(pshader, name, names, minimum, maximum)
#define SHADER_DEFINE(name, names, minimum, maximum) OBJECT_DEFINE(shader, name, names, minimum, maximum)
SHADER_MEMBERS(SHADER_WRAPPER)

#define SHADER_CANVAS(X) \
    X(createShader, "createShader|建立著色器", 2, 2) X(loadShader, "loadShader|載入著色器", 1, 2) X(shader, "shader|著色器", 1, 1) \
    X(resetShader, "resetShader|重設著色器", 0, 0) X(loadTexture, "loadTexture|載入紋理", 6, 6)
SHADER_CANVAS(CANVAS_WRAPPERS)

void gfx_shaders_register(aqua_class *module, aqua_class *canvas, aqua_class *shader) {
    SHADER_MEMBERS(SHADER_DEFINE)
    SHADER_CANVAS(CANVAS_DEFINE)
}
