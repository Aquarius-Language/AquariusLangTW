/* WGSL shaders: the UserUniforms layout (group 1), named textures and storage buffers (group 2), validation through an
   error scope, and the render pipelines each shader needs (cached by state). */
#include "gpu.h"
#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum { FIELD_F32, FIELD_I32, FIELD_U32, FIELD_VECTOR, FIELD_MATRIX };
typedef struct uniform_field { char name[64]; int offset, count, type; } uniform_field;
typedef struct texture_field { char name[64]; int binding; bool cube; gpu_texture *texture; } texture_field;
typedef struct buffer_field { char name[64]; int binding; gpu_buffer *buffer; } buffer_field;
typedef struct pipeline_entry {
    int mode, blend, depth, stencil, cull; uint32_t samples; WGPUTextureFormat format; bool vertex_buffer;
    WGPURenderPipeline pipeline;
} pipeline_entry;

struct gpu_shader {
    gpu_resource link;
    WGPUShaderModule vertex, fragment;
    WGPUBindGroupLayout texture_layout;
    WGPUPipelineLayout pipeline_layout;
    WGPUSampler texture_sampler;
    uniform_field fields[64]; int field_count;
    texture_field textures[8]; int texture_count;
    buffer_field buffers[7]; int buffer_count;
    uint8_t user[GPU_USER_BYTES];
    int version, user_version;
    WGPUBuffer user_buffer;
    WGPUBindGroup user_group, texture_group;
    pipeline_entry *pipelines; size_t pipeline_count, pipeline_capacity;
    bool released;
    /* creation */
    gpu_shader_done done; void *done_data;
    volatile bool validated;
    bool waiting, failed;
    char failure[1536];
};

/* ---- Source scanning ---- */

/* The source without comments. */
static char *strip_comments(const char *source) {
    size_t n = strlen(source);
    char *out = malloc(n + 1), *o = out;
    for (size_t i = 0; i < n;) {
        if (source[i] == '/' && source[i + 1] == '*') {
            const char *end = strstr(source + i + 2, "*/");
            i = end ? (size_t)(end - source) + 2 : n;
        } else if (source[i] == '/' && source[i + 1] == '/') {
            while (i < n && source[i] != '\n' && source[i] != '\r') i++;
        } else *o++ = source[i++];
    }
    *o = 0;
    return out;
}

static const char *skip_space(const char *p) { while (isspace((unsigned char)*p)) p++; return p; }
/* Keep UTF-8 identifier bytes together; the WGSL compiler validates the identifier's Unicode characters. */
static bool word_char(char c) { return (unsigned char)c >= 0x80 || isalnum((unsigned char)c) || c == '_'; }
static const char *identifier(const char *p, char *out, size_t size) {
    size_t n = 0;
    while (word_char(*p)) { if (n + 1 < size) out[n++] = *p; p++; }
    out[n] = 0;
    return p;
}
static char *trim(char *s) {
    while (isspace((unsigned char)*s)) s++;
    char *end = s + strlen(s);
    while (end > s && isspace((unsigned char)end[-1])) *--end = 0;
    return s;
}
static void remove_space(char *s) {
    char *o = s;
    for (; *s; s++) if (!isspace((unsigned char)*s)) *o++ = *s;
    *o = 0;
}

static bool parse_uniforms(gpu_shader *s, const char *source, char *error, size_t error_size) {
    const char *p = source;
    while ((p = strstr(p, "struct")) != NULL) {
        if (p != source && word_char(p[-1])) { p += 6; continue; }
        const char *q = p + 6;
        if (!isspace((unsigned char)*q)) { p = q; continue; }
        char name[64];
        q = identifier(skip_space(q), name, sizeof name);
        q = skip_space(q);
        if (strcmp(name, "UserUniforms") != 0 || *q != '{') { p = q; continue; }
        const char *end = strchr(q + 1, '}');
        if (!end) return true; /* no complete declaration: the compiler reports it */
        size_t length = (size_t)(end - q - 1);
        char *body = malloc(length + 1);
        memcpy(body, q + 1, length);
        body[length] = 0;
        int offset = 0;
        bool ok = true;
        for (char *field = strtok(body, ","); field && ok; field = strtok(NULL, ",")) {
            char *f = trim(field);
            if (!*f) continue;
            char field_name[64], type[64];
            const char *r = identifier(f, field_name, sizeof field_name);
            r = skip_space(r);
            ok = *field_name && *r == ':';
            if (ok) {
                snprintf(type, sizeof type, "%s", r + 1);
                char *t = trim(type);
                int count = 0, kind = 0;
                if (strcmp(t, "f32") == 0) { count = 1; kind = FIELD_F32; }
                else if (strcmp(t, "i32") == 0) { count = 1; kind = FIELD_I32; }
                else if (strcmp(t, "u32") == 0) { count = 1; kind = FIELD_U32; }
                else if (strcmp(t, "vec2<f32>") == 0) { count = 2; kind = FIELD_VECTOR; }
                else if (strcmp(t, "vec3<f32>") == 0) { count = 3; kind = FIELD_VECTOR; }
                else if (strcmp(t, "vec4<f32>") == 0) { count = 4; kind = FIELD_VECTOR; }
                else if (strcmp(t, "mat4x4<f32>") == 0) { count = 16; kind = FIELD_MATRIX; }
                ok = count > 0;
                if (ok) {
                    int alignment = count == 1 ? 4 : count == 2 ? 8 : 16;
                    offset = (offset + alignment - 1) / alignment * alignment;
                    bool duplicate = false;
                    for (int i = 0; i < s->field_count; i++) if (strcmp(s->fields[i].name, field_name) == 0) duplicate = true;
                    if (offset + count * 4 > GPU_USER_BYTES || duplicate || s->field_count == 64) {
                        snprintf(error, error_size, "Duplicate or oversized UserUniforms field.");
                        free(body);
                        return false;
                    }
                    uniform_field *u = &s->fields[s->field_count++];
                    snprintf(u->name, sizeof u->name, "%s", field_name);
                    u->offset = offset; u->count = count; u->type = kind;
                    offset += count * 4;
                }
            }
        }
        free(body);
        if (!ok) { snprintf(error, error_size, "UserUniforms supports f32, i32, u32, vec2/3/4<f32> and mat4x4<f32> fields."); return false; }
        return true;
    }
    return true;
}

/* Parses "@group(N)" or "@binding(N)" at p; returns the end or NULL. */
static const char *attribute(const char *p, bool *is_group, int *number) {
    if (*p != '@') return NULL;
    p++;
    if (strncmp(p, "group", 5) == 0 && !word_char(p[5])) { *is_group = true; p += 5; }
    else if (strncmp(p, "binding", 7) == 0 && !word_char(p[7])) { *is_group = false; p += 7; }
    else return NULL;
    p = skip_space(p);
    if (*p != '(') return NULL;
    p = skip_space(p + 1);
    if (!isdigit((unsigned char)*p)) return NULL;
    long n = 0;
    while (isdigit((unsigned char)*p)) { n = n * 10 + (*p - '0'); if (n > 1000000) n = 1000000; p++; }
    p = skip_space(p);
    if (*p != ')') return NULL;
    *number = (int)n;
    return p + 1;
}

static bool parse_bindings(gpu_shader *s, const char *source, char *error, size_t error_size) {
    bool used[64] = {0};
    for (const char *p = strchr(source, '@'); p; p = strchr(p + 1, '@')) {
        bool first_group, second_group;
        int first, second;
        const char *q = attribute(p, &first_group, &first);
        if (!q) continue;
        q = skip_space(q);
        q = attribute(q, &second_group, &second);
        if (!q || first_group == second_group) continue;
        q = skip_space(q);
        if (strncmp(q, "var", 3) != 0 || word_char(q[3])) continue;
        q += 3;
        int group = first_group ? first : second, binding = first_group ? second : first;
        const char *after = skip_space(q);
        if (*after == '<') {
            const char *close = strchr(after, '>');
            if (!close) continue;
            char space[64];
            size_t n = (size_t)(close - after - 1) < sizeof space - 1 ? (size_t)(close - after - 1) : sizeof space - 1;
            memcpy(space, after + 1, n); space[n] = 0;
            remove_space(space);
            char name[64];
            const char *r = identifier(skip_space(close + 1), name, sizeof name);
            if (!*name || *skip_space(r) != ':') continue;
            if (group != 2) continue;
            if (binding < 9 || binding > 15 || (strcmp(space, "storage") != 0 && strcmp(space, "storage,read") != 0)) {
                snprintf(error, error_size, "Group 2 storage buffers must be var<storage, read> at bindings 9..15.");
                return false;
            }
            if (used[binding]) { snprintf(error, error_size, "Duplicate texture group binding."); return false; }
            used[binding] = true;
            for (int i = 0; i < s->buffer_count; i++)
                if (strcmp(s->buffers[i].name, name) == 0) { snprintf(error, error_size, "Duplicate storage buffer name."); return false; }
            buffer_field *b = &s->buffers[s->buffer_count++];
            snprintf(b->name, sizeof b->name, "%s", name);
            b->binding = binding;
        } else if (after != q) {
            char name[64];
            const char *r = identifier(after, name, sizeof name);
            r = skip_space(r);
            if (!*name || *r != ':') continue;
            const char *semicolon = strchr(r + 1, ';');
            if (!semicolon) continue;
            char type[128];
            size_t n = (size_t)(semicolon - r - 1) < sizeof type - 1 ? (size_t)(semicolon - r - 1) : sizeof type - 1;
            memcpy(type, r + 1, n); type[n] = 0;
            remove_space(type);
            if (group != 2) continue;
            if (binding < 64 && used[binding]) { snprintf(error, error_size, "Duplicate texture group binding."); return false; }
            if (binding < 64) used[binding] = true;
            if (binding == 8 && strcmp(type, "sampler") == 0) continue;
            bool cube = strcmp(type, "texture_cube<f32>") == 0;
            if (binding < 0 || binding > 7 || (!cube && strcmp(type, "texture_2d<f32>") != 0)) {
                snprintf(error, error_size, "Group 2 supports 2D/cube float textures at 0..7 and a sampler at 8.");
                return false;
            }
            for (int i = 0; i < s->texture_count; i++)
                if (strcmp(s->textures[i].name, name) == 0) { snprintf(error, error_size, "Duplicate texture name."); return false; }
            texture_field *t = &s->textures[s->texture_count++];
            snprintf(t->name, sizeof t->name, "%s", name);
            t->binding = binding; t->cube = cube;
        }
    }
    return true;
}

/* ---- Creation ---- */

static void create_layouts(gpu_shader *s, gpu_device *device) {
    WGPUBindGroupLayoutEntry entries[16];
    int n = 0;
    for (int i = 0; i < s->texture_count; i++) {
        WGPUBindGroupLayoutEntry e = WGPU_BIND_GROUP_LAYOUT_ENTRY_INIT;
        e.binding = (uint32_t)s->textures[i].binding;
        e.visibility = WGPUShaderStage_Vertex | WGPUShaderStage_Fragment;
        e.texture.sampleType = WGPUTextureSampleType_Float;
        e.texture.viewDimension = s->textures[i].cube ? WGPUTextureViewDimension_Cube : WGPUTextureViewDimension_2D;
        entries[n++] = e;
    }
    for (int i = 0; i < s->buffer_count; i++) {
        WGPUBindGroupLayoutEntry e = WGPU_BIND_GROUP_LAYOUT_ENTRY_INIT;
        e.binding = (uint32_t)s->buffers[i].binding;
        e.visibility = WGPUShaderStage_Vertex | WGPUShaderStage_Fragment;
        e.buffer.type = WGPUBufferBindingType_ReadOnlyStorage;
        entries[n++] = e;
    }
    WGPUBindGroupLayoutEntry sampler = WGPU_BIND_GROUP_LAYOUT_ENTRY_INIT;
    sampler.binding = 8;
    sampler.visibility = WGPUShaderStage_Vertex | WGPUShaderStage_Fragment;
    sampler.sampler.type = WGPUSamplerBindingType_Filtering;
    entries[n++] = sampler;
    WGPUBindGroupLayoutDescriptor layout = WGPU_BIND_GROUP_LAYOUT_DESCRIPTOR_INIT;
    layout.entryCount = (size_t)n; layout.entries = entries;
    s->texture_layout = wgpuDeviceCreateBindGroupLayout(gpu_device_handle(device), &layout);
    WGPUBindGroupLayout layouts[3] = {gpu_device_drawing_layout(device), gpu_device_user_layout(device), s->texture_layout};
    WGPUPipelineLayoutDescriptor pipeline = WGPU_PIPELINE_LAYOUT_DESCRIPTOR_INIT;
    pipeline.bindGroupLayoutCount = 3; pipeline.bindGroupLayouts = layouts;
    s->pipeline_layout = wgpuDeviceCreatePipelineLayout(gpu_device_handle(device), &pipeline);
    WGPUSamplerDescriptor descriptor = WGPU_SAMPLER_DESCRIPTOR_INIT;
    descriptor.addressModeU = descriptor.addressModeV = descriptor.addressModeW = WGPUAddressMode_ClampToEdge;
    descriptor.magFilter = WGPUFilterMode_Linear; descriptor.minFilter = WGPUFilterMode_Linear;
    descriptor.mipmapFilter = WGPUMipmapFilterMode_Linear;
    descriptor.lodMaxClamp = 32; descriptor.maxAnisotropy = 1;
    s->texture_sampler = wgpuDeviceCreateSampler(gpu_device_handle(device), &descriptor);
}

static WGPUShaderModule module(gpu_device *device, const char *source) {
    size_t a = strlen(gpu_wgsl_header), b = strlen(source);
    char *text = malloc(a + b + 1);
    memcpy(text, gpu_wgsl_header, a);
    memcpy(text + a, source, b + 1);
    WGPUShaderModule m = gpu_device_shader_module(device, text, a + b);
    free(text);
    return m;
}

static void complete(gpu_shader *s, const char *failure) {
    gpu_shader_done done = s->done;
    void *data = s->done_data;
    s->done = NULL;
    if (failure || !s->vertex || !s->fragment) {
        char message[2048];
        snprintf(message, sizeof message, "Shader compile failed: %s", failure ? failure : "wgpu did not complete validation.");
        gpu_shader_free(s);
        if (done) done(NULL, message, data);
        return;
    }
    if (done) done(s, NULL, data);
}

/* Records the validation result; a synchronous wait (native) completes after it returns, the web completes here. */
static void finish(gpu_shader *s, const char *failure) {
    if (s->validated) return;
    s->validated = true;
    if (failure) snprintf(s->failure, sizeof s->failure, "%s", failure);
    s->failed = failure != NULL;
    if (!s->waiting) complete(s, s->failed ? s->failure : NULL);
}

static void on_scope(WGPUPopErrorScopeStatus status, WGPUErrorType type, WGPUStringView message, void *user, void *unused) {
    (void)unused;
    gpu_shader *s = user;
    if (status != WGPUPopErrorScopeStatus_Success) { finish(s, "wgpu did not complete validation."); return; }
    if (type != WGPUErrorType_NoError) {
        char failure[1536];
        int length = message.data ? (int)(message.length == WGPU_STRLEN ? strlen(message.data) : message.length) : 0;
        snprintf(failure, sizeof failure, "%.*s", length, message.data ? message.data : "");
        finish(s, failure);
        return;
    }
    finish(s, NULL);
}

void gpu_shader_create(gpu_device *device, const char *vertex, const char *fragment, gpu_shader_done done, void *data) {
    gpu_shader *s = calloc(1, sizeof *s);
    s->link.next = s->link.previous = &s->link;
    s->user_version = -1;
    s->done = done; s->done_data = data;
    char error[512];
    size_t a = strlen(vertex), b = strlen(fragment);
    char *joined = malloc(a + b + 2);
    memcpy(joined, vertex, a); joined[a] = '\n'; memcpy(joined + a + 1, fragment, b + 1);
    char *clean = strip_comments(joined);
    free(joined);
    bool parsed = parse_uniforms(s, clean, error, sizeof error) && parse_bindings(s, clean, error, sizeof error);
    free(clean);
    if (!parsed) { free(s); done(NULL, error, data); return; }
    gpu_device_link(device, &s->link, GPU_SHADER);
    create_layouts(s, device);
    WGPUDevice handle = gpu_device_handle(device);
    wgpuDevicePushErrorScope(handle, WGPUErrorFilter_Validation);
    s->vertex = module(device, vertex);
    s->fragment = module(device, fragment);
    WGPUPopErrorScopeCallbackInfo callback = WGPU_POP_ERROR_SCOPE_CALLBACK_INFO_INIT;
    callback.mode = GPU_CALLBACK_MODE; callback.callback = on_scope; callback.userdata1 = s;
#if defined(AQUA_OS_WEB)
    wgpuDevicePopErrorScope(handle, callback);
#else
    s->waiting = true;
    wgpuDevicePopErrorScope(handle, callback);
    gpu_device_wait(device, &s->validated);
    s->waiting = false;
    if (!s->validated) { s->validated = true; s->failed = true; snprintf(s->failure, sizeof s->failure, "wgpu did not complete validation."); }
    complete(s, s->failed ? s->failure : NULL);
#endif
}

void gpu_shader_release_handles(gpu_shader *s) {
    for (size_t i = 0; i < s->pipeline_count; i++) wgpuRenderPipelineRelease(s->pipelines[i].pipeline);
    s->pipeline_count = 0;
    if (s->link.device && !gpu_device_disposed(s->link.device)) {
        gpu_device_retire_group(s->link.device, s->user_group);
        gpu_device_retire_buffer(s->link.device, s->user_buffer);
        gpu_device_retire_group(s->link.device, s->texture_group);
    } else {
        if (s->user_group) wgpuBindGroupRelease(s->user_group);
        if (s->user_buffer) wgpuBufferRelease(s->user_buffer);
        if (s->texture_group) wgpuBindGroupRelease(s->texture_group);
    }
    s->user_group = NULL; s->user_buffer = NULL; s->texture_group = NULL;
    if (s->vertex) wgpuShaderModuleRelease(s->vertex);
    if (s->fragment) wgpuShaderModuleRelease(s->fragment);
    if (s->pipeline_layout) wgpuPipelineLayoutRelease(s->pipeline_layout);
    if (s->texture_layout) wgpuBindGroupLayoutRelease(s->texture_layout);
    if (s->texture_sampler) wgpuSamplerRelease(s->texture_sampler);
    s->vertex = s->fragment = NULL; s->pipeline_layout = NULL; s->texture_layout = NULL; s->texture_sampler = NULL;
    s->released = true;
}

void gpu_shader_free(gpu_shader *s) {
    if (!s) return;
    if (s->link.device && !gpu_device_disposed(s->link.device) && !s->released) gpu_device_flush(s->link.device);
    gpu_shader_release_handles(s);
    gpu_device_unlink(&s->link);
    free(s->pipelines);
    free(s);
}

bool gpu_shader_released(const gpu_shader *s) { return !s || s->released; }
gpu_device *gpu_shader_device(const gpu_shader *s) { return s->link.device; }

/* ---- Uniforms, textures and buffers ---- */

static uniform_field *field(gpu_shader *s, const char *name) {
    for (int i = 0; i < s->field_count; i++) if (strcmp(s->fields[i].name, name) == 0) return &s->fields[i];
    return NULL;
}

bool gpu_shader_set(gpu_shader *s, const char *name, const float *values, int count, char *error, size_t error_size) {
    uniform_field *f = field(s, name);
    if (!f) { snprintf(error, error_size, "Unknown UserUniforms field: %s", name); return false; }
    if (count != f->count) { snprintf(error, error_size, "Uniform %s needs %d values.", name, f->count); return false; }
    if (f->type == FIELD_I32 || f->type == FIELD_U32) { snprintf(error, error_size, "Uniform %s requires setInt().", name); return false; }
    memcpy(s->user + f->offset, values, (size_t)count * 4);
    s->version++;
    return true;
}

bool gpu_shader_set_int(gpu_shader *s, const char *name, int32_t value, char *error, size_t error_size) {
    uniform_field *f = field(s, name);
    if (!f) { snprintf(error, error_size, "Unknown UserUniforms field: %s", name); return false; }
    if (f->count != 1) { snprintf(error, error_size, "Uniform %s needs %d values.", name, f->count); return false; }
    if (f->type != FIELD_I32 && f->type != FIELD_U32) { snprintf(error, error_size, "Uniform %s requires set().", name); return false; }
    if (f->type == FIELD_U32 && value < 0) { snprintf(error, error_size, "u32 uniform must be nonnegative."); return false; }
    memcpy(s->user + f->offset, &value, 4);
    s->version++;
    return true;
}

static void forget_texture_group(gpu_shader *s) {
    if (s->texture_group && s->link.device) gpu_device_retire_group(s->link.device, s->texture_group);
    s->texture_group = NULL;
}

bool gpu_shader_set_texture(gpu_shader *s, const char *name, gpu_texture *texture, char *error, size_t error_size) {
    if (s->released || gpu_texture_released(texture)) { snprintf(error, error_size, "Shader or texture is disposed."); return false; }
    for (int i = 0; i < s->texture_count; i++) {
        texture_field *t = &s->textures[i];
        if (strcmp(t->name, name) != 0) continue;
        if (gpu_texture_device(texture) != s->link.device || t->cube != gpu_texture_cube(texture)) break;
        t->texture = texture;
        forget_texture_group(s);
        return true;
    }
    snprintf(error, error_size, "Unknown texture name, foreign device or incompatible texture dimension.");
    return false;
}

bool gpu_shader_set_buffer(gpu_shader *s, const char *name, gpu_buffer *buffer, char *error, size_t error_size) {
    if (s->released) { snprintf(error, error_size, "Shader is disposed."); return false; }
    if (gpu_buffer_released(buffer)) { snprintf(error, error_size, "wgpu buffer has been disposed."); return false; }
    for (int i = 0; i < s->buffer_count; i++) {
        buffer_field *b = &s->buffers[i];
        if (strcmp(b->name, name) != 0 || gpu_buffer_device(buffer) != s->link.device) continue;
        b->buffer = buffer;
        forget_texture_group(s);
        return true;
    }
    snprintf(error, error_size, "Unknown storage buffer name or foreign device.");
    return false;
}

/* User uniforms are uploaded once per Set version and shared by later draws. */
WGPUBindGroup gpu_shader_user_group(gpu_shader *s) {
    if (s->user_group && s->user_version == s->version) return s->user_group;
    gpu_device *d = s->link.device;
    gpu_device_retire_group(d, s->user_group);
    gpu_device_retire_buffer(d, s->user_buffer);
    s->user_buffer = gpu_device_buffer(d, GPU_USER_BYTES, WGPUBufferUsage_Uniform, s->user);
    WGPUBindGroupEntry entry = WGPU_BIND_GROUP_ENTRY_INIT;
    entry.binding = 0; entry.buffer = s->user_buffer; entry.size = GPU_USER_BYTES;
    WGPUBindGroupDescriptor descriptor = WGPU_BIND_GROUP_DESCRIPTOR_INIT;
    descriptor.layout = gpu_device_user_layout(d); descriptor.entryCount = 1; descriptor.entries = &entry;
    s->user_group = wgpuDeviceCreateBindGroup(gpu_device_handle(d), &descriptor);
    s->user_version = s->version;
    return s->user_group;
}

WGPUBindGroup gpu_shader_texture_group(gpu_shader *s, gpu_target *destination, char *error, size_t error_size) {
    for (int i = 0; i < s->texture_count; i++) {
        texture_field *t = &s->textures[i];
        if (!t->texture || gpu_texture_released(t->texture)) { snprintf(error, error_size, "Missing or disposed shader texture: %s", t->name); return NULL; }
        gpu_target *source = gpu_texture_source(t->texture);
        if (source) {
            if (source == destination) { snprintf(error, error_size, "A render target cannot sample its own color attachment."); return NULL; }
            gpu_target_flush(source);
        }
    }
    for (int i = 0; i < s->buffer_count; i++) {
        buffer_field *b = &s->buffers[i];
        if (!b->buffer || gpu_buffer_released(b->buffer)) { snprintf(error, error_size, "Missing or disposed shader storage buffer: %s", b->name); return NULL; }
    }
    if (s->texture_group) return s->texture_group;
    WGPUBindGroupEntry entries[16];
    int n = 0;
    for (int i = 0; i < s->texture_count; i++) {
        WGPUBindGroupEntry e = WGPU_BIND_GROUP_ENTRY_INIT;
        e.binding = (uint32_t)s->textures[i].binding; e.textureView = gpu_texture_view(s->textures[i].texture);
        entries[n++] = e;
    }
    for (int i = 0; i < s->buffer_count; i++) {
        WGPUBindGroupEntry e = WGPU_BIND_GROUP_ENTRY_INIT;
        e.binding = (uint32_t)s->buffers[i].binding; e.buffer = gpu_buffer_handle(s->buffers[i].buffer);
        e.size = (uint64_t)gpu_buffer_length(s->buffers[i].buffer) * 4;
        entries[n++] = e;
    }
    WGPUBindGroupEntry sampler = WGPU_BIND_GROUP_ENTRY_INIT;
    sampler.binding = 8; sampler.sampler = s->texture_sampler;
    entries[n++] = sampler;
    WGPUBindGroupDescriptor descriptor = WGPU_BIND_GROUP_DESCRIPTOR_INIT;
    descriptor.layout = s->texture_layout; descriptor.entryCount = (size_t)n; descriptor.entries = entries;
    s->texture_group = wgpuDeviceCreateBindGroup(gpu_device_handle(s->link.device), &descriptor);
    return s->texture_group;
}

/* ---- Pipelines ---- */

WGPURenderPipeline gpu_shader_pipeline(gpu_shader *s, int mode, int blend, int depth_mode, uint32_t samples, int stencil,
                                       WGPUTextureFormat format, int cull, bool vertex_buffer, char *error, size_t error_size) {
    if (s->released) { snprintf(error, error_size, "wgpu shader has been disposed."); return NULL; }
    for (size_t i = 0; i < s->pipeline_count; i++) {
        pipeline_entry *e = &s->pipelines[i];
        if (e->mode == mode && e->blend == blend && e->depth == depth_mode && e->samples == samples && e->stencil == stencil &&
            e->format == format && e->cull == cull && e->vertex_buffer == vertex_buffer) return e->pipeline;
    }
    gpu_device *d = s->link.device;
    bool depth = depth_mode != 0, stencil_write = stencil == 1 || stencil == 2;
    WGPUVertexAttribute attributes[4] = {WGPU_VERTEX_ATTRIBUTE_INIT, WGPU_VERTEX_ATTRIBUTE_INIT, WGPU_VERTEX_ATTRIBUTE_INIT, WGPU_VERTEX_ATTRIBUTE_INIT};
    static const WGPUVertexFormat formats[4] = {WGPUVertexFormat_Float32x3, WGPUVertexFormat_Float32x3, WGPUVertexFormat_Float32x2, WGPUVertexFormat_Float32x4};
    static const uint64_t offsets[4] = {0, 12, 24, 32};
    for (int i = 0; i < 4; i++) { attributes[i].shaderLocation = (uint32_t)i; attributes[i].offset = offsets[i]; attributes[i].format = formats[i]; }
    WGPUVertexBufferLayout buffer = WGPU_VERTEX_BUFFER_LAYOUT_INIT;
    buffer.arrayStride = 48; buffer.stepMode = WGPUVertexStepMode_Vertex; buffer.attributeCount = 4; buffer.attributes = attributes;
    WGPUBlendState blending = WGPU_BLEND_STATE_INIT;
    blending.color.operation = WGPUBlendOperation_Add;
    blending.color.srcFactor = blend == 2 ? WGPUBlendFactor_Dst : (blend >= 3 && blend <= 5) ? WGPUBlendFactor_One : WGPUBlendFactor_SrcAlpha;
    blending.color.dstFactor = blend == 1 ? WGPUBlendFactor_One : (blend == 2 || blend == 4) ? WGPUBlendFactor_Zero :
                               blend == 3 ? WGPUBlendFactor_OneMinusSrc : WGPUBlendFactor_OneMinusSrcAlpha;
    blending.alpha.operation = WGPUBlendOperation_Add;
    blending.alpha.srcFactor = WGPUBlendFactor_One;
    blending.alpha.dstFactor = blend == 1 ? WGPUBlendFactor_One : blend == 4 ? WGPUBlendFactor_Zero : WGPUBlendFactor_OneMinusSrcAlpha;
    /* 6 subtract (destination minus source), 7 lightest and 8 darkest (per channel), 9 exclusion, as Processing's
       OpenGL renderers set them; min/max ignore factors, which WebGPU requires to be one. */
    if (blend == 6) {
        blending.color.operation = WGPUBlendOperation_ReverseSubtract;
        blending.color.srcFactor = WGPUBlendFactor_SrcAlpha; blending.color.dstFactor = WGPUBlendFactor_One;
        blending.alpha.dstFactor = WGPUBlendFactor_One;
    } else if (blend == 7 || blend == 8) {
        blending.color.operation = blend == 7 ? WGPUBlendOperation_Max : WGPUBlendOperation_Min;
        blending.color.srcFactor = blending.color.dstFactor = WGPUBlendFactor_One;
        blending.alpha.operation = WGPUBlendOperation_Max;
        blending.alpha.srcFactor = blending.alpha.dstFactor = WGPUBlendFactor_One;
    } else if (blend == 9) {
        blending.color.srcFactor = WGPUBlendFactor_OneMinusDst; blending.color.dstFactor = WGPUBlendFactor_OneMinusSrc;
        blending.alpha.dstFactor = WGPUBlendFactor_One;
    }
    WGPUColorTargetState color = WGPU_COLOR_TARGET_STATE_INIT;
    color.format = format;
    color.writeMask = stencil_write ? WGPUColorWriteMask_None : WGPUColorWriteMask_All;
    color.blend = &blending;
    WGPUFragmentState fragment = WGPU_FRAGMENT_STATE_INIT;
    fragment.module = s->fragment; fragment.entryPoint = gpu_text("fs_main");
    fragment.targetCount = 1; fragment.targets = &color;
    WGPUStencilFaceState face = WGPU_STENCIL_FACE_STATE_INIT;
    face.compare = stencil == 3 ? WGPUCompareFunction_Equal : WGPUCompareFunction_Always;
    face.failOp = WGPUStencilOperation_Keep; face.depthFailOp = WGPUStencilOperation_Keep;
    face.passOp = stencil_write ? WGPUStencilOperation_Replace : WGPUStencilOperation_Keep;
    WGPUDepthStencilState depth_state = WGPU_DEPTH_STENCIL_STATE_INIT;
    depth_state.format = WGPUTextureFormat_Depth24PlusStencil8;
    depth_state.depthWriteEnabled = depth_mode == 1 && !stencil_write ? WGPUOptionalBool_True : WGPUOptionalBool_False;
    depth_state.depthCompare = depth && !stencil_write ? WGPUCompareFunction_LessEqual : WGPUCompareFunction_Always;
    depth_state.stencilFront = face; depth_state.stencilBack = face;
    depth_state.stencilReadMask = 255; depth_state.stencilWriteMask = stencil_write ? 255u : 0u;
    WGPURenderPipelineDescriptor descriptor = WGPU_RENDER_PIPELINE_DESCRIPTOR_INIT;
    descriptor.layout = s->pipeline_layout;
    descriptor.vertex.module = s->vertex; descriptor.vertex.entryPoint = gpu_text("vs_main");
    descriptor.vertex.bufferCount = vertex_buffer ? 1 : 0; descriptor.vertex.buffers = vertex_buffer ? &buffer : NULL;
    descriptor.fragment = &fragment;
    descriptor.primitive.topology = mode == 0 ? WGPUPrimitiveTopology_PointList : mode == 1 ? WGPUPrimitiveTopology_LineList :
                                    mode == 5 ? WGPUPrimitiveTopology_TriangleStrip : WGPUPrimitiveTopology_TriangleList;
    descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    descriptor.primitive.cullMode = cull == 1 ? WGPUCullMode_Back : cull == 2 ? WGPUCullMode_Front : WGPUCullMode_None;
    descriptor.depthStencil = &depth_state;
    descriptor.multisample.count = samples; descriptor.multisample.mask = 0xFFFFFFFFu;
    WGPURenderPipeline pipeline = wgpuDeviceCreateRenderPipeline(gpu_device_handle(d), &descriptor);
    const char *failure = gpu_device_take_error(d);
    if (failure || !pipeline) {
        if (pipeline) wgpuRenderPipelineRelease(pipeline);
        snprintf(error, error_size, "%s", failure ? failure : "wgpu render pipeline creation failed.");
        return NULL;
    }
    if (s->pipeline_count == s->pipeline_capacity) {
        s->pipeline_capacity = s->pipeline_capacity ? s->pipeline_capacity * 2 : 8;
        s->pipelines = realloc(s->pipelines, s->pipeline_capacity * sizeof *s->pipelines);
    }
    s->pipelines[s->pipeline_count++] = (pipeline_entry){mode, blend, depth_mode, stencil, cull, samples, format, vertex_buffer, pipeline};
    return pipeline;
}
