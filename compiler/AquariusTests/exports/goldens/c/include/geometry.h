/* geometry.h: the C interface of the geometry native library, written by aqua <version>
   from main.aqua (docs/native-libraries.md). Do not edit; rebuild the library instead.

   Values are copied at the boundary. Strings and arrays a function returns belong to the caller: free them with
   geo_free. A failed call returns zero and geo_last_error() gives the reason (NULL after a successful call;
   kept per thread). Calls from several threads are serialized; the first call runs the library's top-level code. */
#ifndef GEOMETRY_H
#define GEOMETRY_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32) && !defined(GEOMETRY_STATIC)
#define GEOMETRY_API __declspec(dllimport)
#else
#define GEOMETRY_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define GEOMETRY_ABI_VERSION 1

typedef struct geo_int_array { int32_t *data; size_t count; } geo_int_array;
typedef struct geo_float_array { float *data; size_t count; } geo_float_array;
typedef struct geo_double_array { double *data; size_t count; } geo_double_array;
typedef struct geo_bool_array { bool *data; size_t count; } geo_bool_array;
typedef struct geo_long_array { int64_t *data; size_t count; } geo_long_array;

/* A point in the plane. */
typedef struct geo_point { double x; double y; } geo_point;

/* A person with a name. */
typedef struct geo_person { char *name; int32_t age; } geo_person;

/* Starts the library (runs its top-level code once). Optional: the first call starts it. Returns false when it fails. */
GEOMETRY_API bool geo_initialize(void);
/* Stops the library and releases everything it holds; a later call starts it again. */
GEOMETRY_API void geo_shutdown(void);
/* Why this thread's last call failed, or NULL when it succeeded. Valid until this thread's next call. */
GEOMETRY_API const char *geo_last_error(void);
/* Frees a string or an array's data returned by this library. */
GEOMETRY_API void geo_free(void *memory);
/* The ABI version of the library (GEOMETRY_ABI_VERSION for this header). */
GEOMETRY_API uint32_t geo_abi_version(void);

/* Area of a rectangle. */
GEOMETRY_API double geo_area(double arg0, double arg1);
/* Distance between two points. */
GEOMETRY_API double geo_distance(geo_point a, geo_point b);
/* The point halfway between two points.
   Both are copied in and the result is copied out. */
GEOMETRY_API geo_point geo_midpoint(geo_point a, geo_point b);
/* Every value multiplied by a factor. */
GEOMETRY_API geo_double_array geo_scale(const double *values, size_t values_count, double factor);
GEOMETRY_API int32_t geo_sum(const int32_t *values, size_t values_count);
GEOMETRY_API float geo_halve(float x);
GEOMETRY_API bool geo_is_even(int32_t n);
GEOMETRY_API geo_bool_array geo_evens(int32_t n);
GEOMETRY_API geo_long_array geo_squares(int32_t n);
GEOMETRY_API float geo_widths(const float *values, size_t values_count);
GEOMETRY_API int64_t geo_total(const int64_t *values, size_t values_count);
GEOMETRY_API int32_t geo_count_true(const bool *values, size_t values_count);
/* Greets someone by name. */
GEOMETRY_API char *geo_greet(const char *arg0);
GEOMETRY_API int64_t geo_big(int64_t n);
GEOMETRY_API geo_person geo_make_person(const char *name, int32_t age);
GEOMETRY_API char *geo_describe(geo_person p);
GEOMETRY_API void geo_log(const char *message);
GEOMETRY_API int32_t geo_divide(int32_t a, int32_t b);
GEOMETRY_API int32_t geo_next(void);

#ifdef __cplusplus
}
#endif

#endif
