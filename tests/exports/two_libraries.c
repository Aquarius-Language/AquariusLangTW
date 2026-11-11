/* Two native libraries in one process (geometry and counter): each holds its own instance and state
   (scripts/test-exports.ps1). */
#include "counter.h"
#include "geometry.h"
#include <stdio.h>

int main(void) {
    int a = geo_next(), b = counter_increment(), c = geo_next(), d = counter_increment();
    printf("two libraries %d %d %d %d\n", a, b, c, d);
    counter_shutdown();
    printf("after one shutdown %d %d\n", geo_next(), counter_increment());
    geo_shutdown();
    counter_shutdown();
    return !(a == 1 && b == 101 && c == 2 && d == 102);
}
