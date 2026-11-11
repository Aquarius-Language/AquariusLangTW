/* Eight threads call the geometry library at once: calls are serialized, every result is right and the counter sees
   every call exactly once; each thread's last error is its own (scripts/test-exports.ps1). */
#include "geometry.h"
#include <pthread.h>
#include <stdio.h>
#include <string.h>

enum { THREADS = 8, CALLS = 250 };
static int failures[THREADS];

static void *worker(void *argument) {
    int index = (int)(size_t)argument;
    for (int i = 0; i < CALLS; i++) {
        if (geo_area(index, i) != (double)index * i) failures[index]++;
        if (geo_next() <= 0) failures[index]++;
        char *text = geo_greet("thread");
        if (!text || strcmp(text, "你好, thread!") != 0) failures[index]++;
        geo_free(text);
        if (i % 50 == 0) {
            geo_divide(1, 0);
            if (!geo_last_error()) failures[index]++;
        }
        if (geo_last_error() && i % 50 != 0) failures[index]++;
    }
    return NULL;
}

int main(void) {
    pthread_t threads[THREADS];
    for (int t = 0; t < THREADS; t++) pthread_create(&threads[t], NULL, worker, (void *)(size_t)t);
    for (int t = 0; t < THREADS; t++) pthread_join(threads[t], NULL);
    int total = 0;
    for (int t = 0; t < THREADS; t++) total += failures[t];
    int next = geo_next();
    printf("threads %d calls, %d failures, next %d\n", THREADS * CALLS, total, next);
    geo_shutdown();
    return total != 0 || next != THREADS * CALLS + 1;
}
