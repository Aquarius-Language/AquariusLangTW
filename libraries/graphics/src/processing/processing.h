/* Processing (繪圖): shared declarations of the sketch state, canvases (the main canvas and PGraphics), images,
 * styles and the files implementing each area:
 *   module.c     registration, constants and live fields          lifecycle.c  size/run/frames/close
 *   input.c      window events -> sketch events, text input       canvas.c     canvases, camera, drawing core
 *   drawing.c    style, color, transforms, primitives, lights     geometry.c   the tessellation kernel
 *   image.c      PImage, pixels, filters, files                   shapes.c     PShape, loadMesh
 *   shaders.c    PShader, loadShader/createShader/loadTexture     text.c       text, fonts, the raster cache
 *   math.c       math, random, noise                              vector.c     PVector
 * Semantics follow the earlier desktop implementation (System.Numerics matrices, its validation and messages). */
#ifndef GRAPHICS_PROCESSING_H
#define GRAPHICS_PROCESSING_H

#include "../graphics.h"
#include "../common/hashmap.h"
#include "../common/matrix.h"
#include "../gpu/gpu.h"
#include "geometry.h"

typedef struct canvas canvas;
typedef struct pimage pimage;
typedef struct pshader pshader;

/* ---- Classes (registered once per process in module.c) ---- */
extern aqua_class *gfx_processing_module_class, *gfx_canvas_class, *gfx_image_class, *gfx_shape_class, *gfx_shader_class, *gfx_vector_class, *gfx_font_class;

/* ---- Styles and vertices ---- */
typedef struct light { int kind; vec3 color, position, direction, falloff, specular; float cutoff, concentration; } light;
typedef struct style {
    vec4 fill, stroke, tint;
    bool has_fill, has_stroke, lit;
    float weight, text_size, leading, shininess;
    int rect_mode, ellipse_mode, image_mode, color_mode, text_align, text_vertical, cap, join;
    vec4 color_max;
    vec3 ambient, material_ambient, specular, emissive, falloff, light_specular;
    light lights[8];
    int light_count;
    char font[256];
} style;
void gfx_style_default(style *s);

/* 12 floats, the GPU vertex layout. */
typedef struct pvertex { vec3 position, normal; vec2 uv; vec4 color; } pvertex;

/* ---- Images ---- */
struct pimage {
    processing *owner;
    int width, height;
    uint8_t *bytes;           /* RGBA8, straight alpha (NULL for canvas surfaces) */
    gpu_texture *texture;     /* uploaded copy, or a canvas's color attachment */
    canvas *canvas;           /* the PGraphics this image is the surface of */
    bool dirty, flipped, disposed, premultiplied;
    aqua_ref pixels;          /* the array loadPixels() published */
    pimage *previous, *next;  /* the owner's live images */
};
pimage *gfx_image_new(processing *p, int width, int height); /* zeroed RGBA; NULL above the limits */
pimage *gfx_image_detached(processing *p, int width, int height); /* not on the live list (text rasters, scratch) */
void gfx_image_finalize(aqua_runtime *rt, void *data);
void gfx_image_dispose(pimage *image);                      /* releases the GPU copy and marks it disposed */
void gfx_image_free(pimage *image);
bool gfx_image_upload(pimage *image, gpu_device *device, char *error, size_t size);
aqua_value gfx_image_object(aqua_runtime *rt, pimage *image);
/* A live PImage or PGraphics argument ("Expected a live PImage or PGraphics from this runtime."). */
pimage *gfx_image_argument(aqua_call *call, processing *p, uint32_t index);
pimage *gfx_image_of_value(processing *p, aqua_value value);
/* The pixels of an image or canvas (canvases are read from the GPU); `done` gets straight RGBA rows. */
typedef void (*pixels_done)(void *data, const uint8_t *rgba, int width, int height, const char *error);
void gfx_image_pixels(pimage *image, pixels_done done, void *data);
uint32_t gfx_pixel_at(const uint8_t *bytes, int width, int height, int x, int y);
void gfx_pixel_set(uint8_t *bytes, int width, int height, int x, int y, uint32_t color);
aqua_value gfx_pixel_array(aqua_runtime *rt, const uint8_t *bytes, int count);
bool gfx_pixel_bytes(aqua_call *call, aqua_value array, int count, uint8_t *out); /* "pixels must contain N packed ARGB colors." */
void gfx_image_filter(uint8_t *bytes, int width, int height, int mode, float parameter);
bool gfx_image_write_file(const char *path, int width, int height, const uint8_t *rgba, char *error, size_t size);
pimage *gfx_image_load(aqua_call *call, processing *p, const char *path);

/* ---- Canvases ---- */
struct canvas {
    processing *owner;
    bool offscreen, is3d, drawing, disposed, smooth;
    gpu_target *target;
    int width, height, pixel_width, pixel_height;
    pimage *surface;
    style style;
    mat4 model, view, projection;
    mat4 *matrices; size_t matrix_count, matrix_capacity;
    style *styles; size_t style_count, style_capacity;
    pshader *custom; aqua_ref custom_ref;
    int detail, sphere_u, sphere_v, shape_mode, texture_mode, blend_mode;
    float tightness;
    pvertex *vertices; size_t vertex_count, vertex_capacity;
    pvertex *contour_vertices; size_t contour_vertex_count, contour_vertex_capacity;
    size_t *contour_starts; size_t contour_count, contour_capacity;
    bool in_contour;
    vec3 *curve; size_t curve_count, curve_capacity;
    pimage *texture; aqua_ref texture_ref;
    vec3 normal;
    WGPUTextureFormat color_format;
    bool has_clip;
    float clip[4];
    aqua_ref pixels;
    canvas *previous, *next;  /* the owner's offscreen canvases */
};

canvas *gfx_canvas_new(processing *p, int width, int height, bool is3d, bool offscreen);
bool gfx_canvas_initialize(aqua_call *call, canvas *c, gpu_device *device);
void gfx_canvas_dispose(canvas *c);
void gfx_canvas_free(canvas *c);
void gfx_canvas_default_camera(canvas *c);
bool gfx_canvas_resize(canvas *c, int width, int height, int pixel_width, int pixel_height); /* changed? */
void gfx_canvas_bind(canvas *c);
bool gfx_canvas_live(aqua_call *call, canvas *c);              /* "Canvas is disposed." */
bool gfx_canvas_require_readable(aqua_call *call, canvas *c);
bool gfx_canvas_require_drawing(aqua_call *call, canvas *c);
void gfx_canvas_background(canvas *c, vec4 color);
mat4 gfx_canvas_mvp(const canvas *c);
bool gfx_canvas_ortho(aqua_call *call, float l, float r, float b, float t, float n, float f, mat4 *out);
bool gfx_canvas_perspective(aqua_call *call, float fov, float aspect, float near, float far, mat4 *out);
void gfx_canvas_set_clip(canvas *c, float x, float y, float width, float height);
/* Draws vertices with the canvas's matrices, style, lights, blend, clip and shader. */
bool gfx_canvas_draw(aqua_call *call, canvas *c, const pvertex *vertices, size_t count, pimage *image, bool lit, int stencil);
bool gfx_canvas_draw_packed(aqua_call *call, canvas *c, const float *floats, size_t count, pimage *image, bool lit, int stencil);
/* The geometry kernel with this canvas's state, and drawing its last output. */
geo *gfx_canvas_geometry(canvas *c);
bool gfx_canvas_check(aqua_call *call, int result);            /* kernel error codes as language errors */
bool gfx_canvas_draw_geometry(aqua_call *call, canvas *c, geo *g, pimage *image);
bool gfx_canvas_polygon(aqua_call *call, canvas *c, const vec3 *points, int count, bool close, bool fill);
bool gfx_canvas_polyline(aqua_call *call, canvas *c, const vec3 *points, int count, bool close);
bool gfx_canvas_segment(aqua_call *call, canvas *c, vec3 a, vec3 b);
bool gfx_canvas_point(aqua_call *call, canvas *c, vec3 p);
bool gfx_canvas_rect(aqua_call *call, canvas *c, float x, float y, float w, float h, float radius);
bool gfx_canvas_ellipse(aqua_call *call, canvas *c, float x, float y, float w, float h, float start, float stop, int mode, bool arc);
bool gfx_canvas_curve(aqua_call *call, canvas *c, bool catmull, const vec3 *controls);
int gfx_canvas_curve_points(canvas *c, bool catmull, const vec3 *controls, int from, vec3 *out, int capacity);
bool gfx_canvas_image(aqua_call *call, canvas *c, pimage *image, float x, float y, const float *w, const float *h);
bool gfx_canvas_box(aqua_call *call, canvas *c, float w, float h, float d);
bool gfx_canvas_sphere(aqua_call *call, canvas *c, float radius);
bool gfx_canvas_end_shape(aqua_call *call, canvas *c, bool close);
/* Reads the canvas's pixels (straight RGBA at its pixel size). */
void gfx_canvas_read_pixels(canvas *c, pixels_done done, void *data);
/* Replaces the canvas's pixels with `bytes` (pixel_width x pixel_height), drawn as an unlit REPLACE quad. */
bool gfx_canvas_write_pixels(aqua_call *call, canvas *c, const uint8_t *bytes);
pvertex gfx_canvas_vertex(const canvas *c, vec3 position, vec4 color, vec2 uv);
void gfx_canvas_vertices_clear(canvas *c);
void gfx_canvas_push_vertex(canvas *c, pvertex v);           /* into the active contour or the shape */
pvertex *gfx_canvas_active_vertices(canvas *c, size_t *count);

/* ---- Shaders ---- */
struct pshader { processing *owner; gpu_shader *shader; aqua_ref textures[8]; char texture_names[8][64]; bool disposed; pshader *previous, *next; };
pshader *gfx_shader_of_value(processing *p, aqua_value value);
void gfx_shader_dispose(pshader *s);
gpu_shader *gfx_processing_default_shader(processing *p);

/* ---- Sketch events ---- */
enum {
    EVENT_MOUSE_PRESSED, EVENT_MOUSE_RELEASED, EVENT_MOUSE_CLICKED, EVENT_MOUSE_MOVED, EVENT_MOUSE_DRAGGED,
    EVENT_KEY_PRESSED, EVENT_KEY_RELEASED, EVENT_KEY_TYPED, EVENT_MOUSE_WHEEL, EVENT_WINDOW_RESIZED, EVENT_TEXT_INPUT,
    EVENT_COMPOSITION_STARTED, EVENT_COMPOSITION_UPDATED, EVENT_COMPOSITION_ENDED, EVENT_ERROR, EVENT_COUNT,
    EVENT_APPLY = EVENT_COUNT   /* a queue entry that only applies its fields */
};
extern const char *const gfx_event_names[EVENT_COUNT];

/* The fields scripts read during events (snapshotted per queued event). */
typedef struct input_fields {
    double mouse_x, mouse_y, pmouse_x, pmouse_y, key_timestamp, wheel_count;
    int mouse_button, key_code;
    bool mouse_pressed, key_pressed;
    char key[8];
} input_fields;

typedef struct sketch_event {
    int event;
    input_fields fields;
    char *text;          /* textInput / compositionUpdated text */
    int cursor;
    double timestamp;
} sketch_event;

/* A window event kept until the next frame (aqua/window.h types). */
typedef struct raw_event { int type, key, action, button, cursor; double x, y, time; char *text; } raw_event;

/* ---- The per-runtime Processing state ---- */
struct processing {
    aqua_runtime *rt;
    /* sketch */
    aqua_window *window;
    gpu_device *device;
    gpu_shader *default_shader;
    canvas *screen;
    canvas *canvases;         /* live offscreen canvases */
    pimage *images;           /* live images */
    pshader *shaders;         /* live shaders */
    aqua_ref *textures; size_t texture_count, texture_capacity; /* loadTexture results to release at close */
    bool listening;
    /* the loop */
    bool running, looping, redraw, exiting, close_requested;
    double redraw_at;         /* ms (aqua_now_ms), < 0 for none */
    double target_fps, actual_frame_rate, frame_start, clock_start, clock_stop;
    bool clock_running, has_frame_rate;
    int frame_count, frame_limit;
    aqua_ref setup, draw;
    aqua_ref events[EVENT_COUNT];
    gfx_async *run_call;
    int phase;
    bool pumping, pump_again;
    /* this frame's event dispatch (run() and beginFrame()) */
    size_t dispatch_index;
    bool dispatching, dispatch_waiting;
    void (*dispatch_finished)(processing *p, const char *failure);
    gfx_async *manual_call;   /* a beginFrame()/endFrame() waiting for its events or the next frame */
    char error_event[32];
    char *error_message;
    int failed_event;
    char *failure;            /* the fatal error ending run() */
    /* input */
    input_fields input;
    uint8_t keys[349];
    bool buttons[8];
    double cursor_x, cursor_y;
    double wheel;
    bool moved;
    raw_event *raw; size_t raw_count, raw_capacity;           /* window events since the last frame */
    sketch_event *queue; size_t queue_count, queue_capacity; /* sketch events of this frame */
    bool text_input_enabled, is_composing;
    char *input_text, *composition_text;
    int composition_cursor;
    double input_timestamp;
    /* math */
    struct net_random { int inext, inextp; int seed_array[56]; bool seeded; } random;
    int noise_permutation[512];
    int noise_octaves;
    double noise_falloff;
    /* text */
    gfx_map text_cache;
    int64_t text_frame;
    size_t text_bytes;
    /* geometry */
    geo geometry;
};

processing *gfx_processing_of(aqua_call *call);
processing *gfx_processing_create(aqua_runtime *rt);
bool gfx_require_sketch(aqua_call *call, processing *p);          /* "Call Processing.size() first." */
void gfx_processing_close(processing *p);                         /* releases the sketch's window and GPU resources */
float gfx_processing_content_scale(processing *p);

void gfx_shape_finalize(aqua_runtime *rt, void *data);
void gfx_shader_finalize(aqua_runtime *rt, void *data);
void gfx_font_finalize(aqua_runtime *rt, void *data);
bool gfx_text_measure_line(const char *font, float size, const char *text, size_t length, int *width, int *height);

/* Registration of each file's members onto the module and the PGraphics class. */
void gfx_drawing_register(aqua_class *module, aqua_class *canvas);
void gfx_image_register(aqua_class *module, aqua_class *canvas, aqua_class *image);
void gfx_shapes_register(aqua_class *module, aqua_class *canvas, aqua_class *shape);
void gfx_shaders_register(aqua_class *module, aqua_class *canvas, aqua_class *shader);
void gfx_text_register(aqua_class *module, aqua_class *canvas, aqua_class *font);
void gfx_math_register(aqua_class *module);
void gfx_vector_register(aqua_class *module, aqua_class *vector);
void gfx_lifecycle_register(aqua_class *module, aqua_class *canvas);
void gfx_input_register(aqua_class *module);

/* input.c */
void gfx_input_attach(processing *p);
void gfx_input_detach(processing *p);
void gfx_input_reset(processing *p);
void gfx_input_collect(processing *p);              /* raw window events -> this frame's sketch events */
void gfx_input_apply(processing *p, const sketch_event *e);
void gfx_input_clear_queue(processing *p);
/* text.c */
void gfx_text_end_frame(processing *p);
void gfx_text_clear(processing *p);
/* color.c */
vec4 gfx_color_unpack(uint32_t c);
uint32_t gfx_color_pack(vec4 c);
vec4 gfx_color_hsb(float h, float s, float b, float a);
vec3 gfx_color_rgb_to_hsb(vec4 c);
/* Parses fill()/stroke()/background()-style arguments [start, start + count) with the style's color mode. */
bool gfx_color_arguments(aqua_call *call, const style *s, uint32_t start, uint32_t count, vec4 *out);
/* drawing.c: 16-element column-major matrices (applyMatrix, GLM) */
bool gfx_matrix_argument(aqua_call *call, uint32_t index, mat4 *out);
aqua_value gfx_matrix_value(aqua_runtime *rt, mat4 m);
/* math.c */
void gfx_random_seed(processing *p, int seed);
double gfx_random_next(processing *p);  /* .NET Random.NextDouble */
void gfx_noise_seed(processing *p, int seed);

/* Wrappers of canvas members: one for the module (the main canvas) and one for PGraphics objects. */
#define CANVAS_WRAPPERS(name, names, minimum, maximum) \
    static void module_##name(aqua_call *call) { gfx_enter(call, "Processing.", names); processing *p = gfx_processing_of(call); p_##name(call, p, p->screen); } \
    static void object_##name(aqua_call *call) { gfx_enter(call, "Processing.", names); processing *p = gfx_processing_of(call); p_##name(call, p, (canvas *)aqua_self(call)); }
#define CANVAS_DEFINE(name, names, minimum, maximum) \
    aqua_define_function(module, names, module_##name, gfx_signature(minimum, maximum)); \
    aqua_define_function(canvas, names, object_##name, gfx_signature(minimum, maximum));
/* Module-only members. */
#define MODULE_WRAPPER(name, names, minimum, maximum) \
    static void module_##name(aqua_call *call) { gfx_enter(call, "Processing.", names); p_##name(call, gfx_processing_of(call)); }
#define MODULE_DEFINE(name, names, minimum, maximum) aqua_define_function(module, names, module_##name, gfx_signature(minimum, maximum));
/* Members of other objects (PImage, PShape, PShader, PVector): the implementation gets the object's data. */
#define OBJECT_WRAPPER(type, name, names, minimum, maximum) \
    static void object_##name(aqua_call *call) { gfx_enter(call, "Processing.", names); p_##name(call, gfx_processing_of(call), (type *)aqua_self(call)); }
#define OBJECT_DEFINE(cls, name, names, minimum, maximum) aqua_define_function(cls, names, object_##name, gfx_signature(minimum, maximum));

#endif
