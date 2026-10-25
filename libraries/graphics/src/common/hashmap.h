/* Small containers: growable arrays and a byte-string keyed hash map (the text raster cache). */
#ifndef GRAPHICS_HASHMAP_H
#define GRAPHICS_HASHMAP_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Ensures *items holds at least `needed` elements of `size` bytes, growing geometrically. */
void gfx_grow(void **items, size_t *capacity, size_t needed, size_t size);
#define GFX_RESERVE(items, capacity, needed) gfx_grow((void **)&(items), &(capacity), (needed), sizeof *(items))
#define GFX_PUSH(items, count, capacity, value) \
    do { GFX_RESERVE(items, capacity, (count) + 1); (items)[(count)++] = (value); } while (0)

typedef struct gfx_map_entry { unsigned char *key; size_t length; uint64_t hash; void *value; } gfx_map_entry;
typedef struct gfx_map { gfx_map_entry *entries; size_t capacity, count, used; } gfx_map;

uint64_t gfx_hash(const void *data, size_t length, uint64_t seed);
void *gfx_map_get(const gfx_map *map, const void *key, size_t length);
void gfx_map_put(gfx_map *map, const void *key, size_t length, void *value);
void *gfx_map_remove(gfx_map *map, const void *key, size_t length);
/* Visits every entry; the callback may not modify the map. */
void gfx_map_each(const gfx_map *map, void (*visit)(const gfx_map_entry *entry, void *data), void *data);
void gfx_map_clear(gfx_map *map);

#endif
