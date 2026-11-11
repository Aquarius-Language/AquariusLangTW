/* The consumer of the shapes test library: a [library] built through [export] module (scripts/test-exports.ps1). */
#include "shapes.h"
#include <stdio.h>

int main(void) {
    char *label = shapes_label();
    printf("%s %d\n", label, (int)(shapes_square_area(1.5) * 100));
    shapes_free(label);
    shapes_shutdown();
    return 0;
}
