/* The Processing geometry kernel: triangulation, strokes with joins and caps, ellipses, rounded rectangles, curves and
 * primitive meshes. The earlier hosts ran it as geometry.wasm; it is compiled into the library now, with its state in
 * a context instead of globals. A caller writes the state block and the operation's input, runs an operation and draws
 * the output ranges of 12-float vertices (position, normal, uv, color) in order: FILL ranges are lit, UNLIT_FILL and
 * STROKE ranges are not. */
#ifndef GRAPHICS_GEOMETRY_H
#define GRAPHICS_GEOMETRY_H

enum { GEO_FILL = 0, GEO_UNLIT_FILL = 1, GEO_STROKE = 2 };
enum { GEO_ZERO_AREA = -1, GEO_NOT_SIMPLE = -2, GEO_OUT_OF_MEMORY = -3 };

/* State block layout (floats; integer fields are stored as floats). */
enum {
    GEO_WEIGHT, GEO_CAP, GEO_JOIN, GEO_IS3D, GEO_WIDTH, GEO_HEIGHT,
    GEO_STROKE_COLOR, GEO_FILL_COLOR = GEO_STROKE_COLOR + 4, GEO_NORMAL = GEO_FILL_COLOR + 4,
    GEO_HAS_FILL = GEO_NORMAL + 3, GEO_HAS_STROKE, GEO_RECT_MODE, GEO_ELLIPSE_MODE, GEO_DETAIL, GEO_TIGHTNESS,
    GEO_MVP, GEO_STATE_COUNT = GEO_MVP + 16
};

typedef struct geo_range { int start, count, kind; } geo_range;

typedef struct geo {
    float state[GEO_STATE_COUNT];
    float *input; int input_capacity;
    float *vertices; int vertex_count, vertex_capacity;
    geo_range *ranges; int range_count, range_capacity;
    int *indices; int index_count, index_capacity;
    int failed;
    float inverse[16]; int inverse_ok, inverse_valid;
} geo;

void gfx_geo_free(geo *g);
void gfx_geo_state_changed(geo *g);          /* drops the cached inverse transform */
float *gfx_geo_input(geo *g, int floats);    /* NULL when it cannot grow */

/* Operations return a nonnegative result or an error code. Inputs are documented in geometry.c. */
int gfx_geo_polygon(geo *g, int n, int close, int fill);
int gfx_geo_polygon_holes(geo *g, int n, int holes, int close, int fill);
int gfx_geo_polyline(geo *g, int n, int close);
int gfx_geo_line(geo *g);
int gfx_geo_point(geo *g);
int gfx_geo_rect(geo *g);
int gfx_geo_ellipse(geo *g, int mode, int arc);
int gfx_geo_curve(geo *g, int catmull);
int gfx_geo_curve_points(geo *g, int catmull, int from);
int gfx_geo_box(geo *g);
int gfx_geo_sphere(geo *g, int us, int vs);
int gfx_geo_points(geo *g, int n);
int gfx_geo_lines(geo *g, int n);
int gfx_geo_shape_indices(geo *g, int kind, int n);
int gfx_geo_image(geo *g, int mode, int flipped);

#endif
