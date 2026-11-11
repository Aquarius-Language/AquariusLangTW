/* Starts and stops the geometry library 1000 times; each start runs its top-level code afresh (scripts/test-exports.ps1). */
#include "geometry.h"
#include <stdio.h>

int main(void) {
    for (int round = 0; round < 1000; round++) {
        if (!geo_initialize()) { printf("lifecycle: initialize failed in round %d: %s\n", round, geo_last_error()); return 1; }
        int first = geo_next(), second = geo_next();
        if (first != 1 || second != 2 || geo_area(2, 3) != 6) { printf("lifecycle: wrong results in round %d\n", round); return 1; }
        geo_shutdown();
    }
    geo_shutdown(); /* stopping a stopped library does nothing */
    printf("lifecycle 1000 ok\n");
    return 0;
}
