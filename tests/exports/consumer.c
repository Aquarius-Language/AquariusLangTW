/* The C consumer of the geometry test library (tests/exports/geometry): calls every export through the C header and
   prints the lines of geometry.expected. Numbers print as integer hundredths (truncated), which parity.aqua prints the
   same way. scripts/test-exports.ps1 builds and runs it on every target. */
#include "geometry.h"
#include <stdio.h>

static int hundredths(double x) { return (int)(x * 100); }
static void error_line(const char *what) {
    const char *error = geo_last_error();
    printf("native error %s: %s\n", what, error ? error : "(none)");
}

int main(void) {
    printf("native abi %u\n", (unsigned)geo_abi_version());
    printf("area %d\n", hundredths(geo_area(3, 4.5)));
    geo_point a = {0, 0}, b = {3, 4};
    printf("distance %d\n", hundredths(geo_distance(a, b)));
    geo_point m = geo_midpoint(a, b);
    printf("midpoint %d %d\n", hundredths(m.x), hundredths(m.y));
    const double values[] = {1, 2.5, -4};
    geo_double_array scaled = geo_scale(values, 3, 2);
    printf("scale");
    for (size_t i = 0; i < scaled.count; i++) printf(" %d", hundredths(scaled.data[i]));
    printf("\n");
    geo_free(scaled.data);
    const int32_t ints[] = {1, 2, 3, 4};
    printf("sum %d\n", geo_sum(ints, 4));
    printf("halve %d\n", hundredths(geo_halve(5.0f)));
    printf("isEven %d %d\n", geo_is_even(4), geo_is_even(7));
    geo_bool_array evens = geo_evens(5);
    printf("evens");
    for (size_t i = 0; i < evens.count; i++) printf(" %d", evens.data[i]);
    printf("\n");
    geo_free(evens.data);
    geo_long_array squares = geo_squares(3);
    printf("squares");
    for (size_t i = 0; i < squares.count; i++) printf(" %lld", (long long)squares.data[i]);
    printf("\n");
    geo_free(squares.data);
    const float floats[] = {0.5f, 1.25f};
    printf("widths %d\n", hundredths(geo_widths(floats, 2)));
    const int64_t longs[] = {5000000000LL, 1};
    printf("total %lld\n", (long long)geo_total(longs, 2));
    const bool bools[] = {true, false, true};
    printf("countTrue %d\n", geo_count_true(bools, 3));
    char *greeting = geo_greet("世界");
    printf("greet %s\n", greeting);
    geo_free(greeting);
    printf("big %lld\n", (long long)geo_big(3000000000LL));
    geo_person person = geo_make_person("Ada", 36);
    printf("person %s %d\n", person.name, person.age);
    char *description = geo_describe(person);
    printf("describe %s\n", description);
    geo_free(description);
    geo_free(person.name);
    fflush(stdout); /* the library writes its own output */
    geo_log("hello");
    printf("next %d\n", geo_next());
    printf("next %d\n", geo_next());

    geo_divide(1, 0);
    error_line("divide");
    geo_greet("\xff");
    printf("c-abi error utf8: %s\n", geo_last_error());
    geo_greet(NULL);
    error_line("null string");
    geo_sum(NULL, 3);
    printf("c-abi error null array: %s\n", geo_last_error());
    geo_area(1, 1);
    printf("native error %s\n", geo_last_error() ? "kept" : "cleared");
    geo_shutdown();
    printf("native next after shutdown %d\n", geo_next());
    geo_shutdown();
    return 0;
}
