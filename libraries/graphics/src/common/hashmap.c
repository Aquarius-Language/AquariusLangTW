#include "hashmap.h"
#include <stdlib.h>
#include <string.h>

void gfx_grow(void **items, size_t *capacity, size_t needed, size_t size) {
    if (needed <= *capacity) return;
    size_t next = *capacity ? *capacity * 2 : 8;
    while (next < needed) next *= 2;
    void *grown = realloc(*items, next * size);
    if (!grown) abort(); /* out of memory: the runtime cannot continue meaningfully */
    *items = grown;
    *capacity = next;
}

uint64_t gfx_hash(const void *data, size_t length, uint64_t seed) {
    const unsigned char *bytes = data;
    uint64_t h = 1469598103934665603ull ^ seed;
    for (size_t i = 0; i < length; i++) { h ^= bytes[i]; h *= 1099511628211ull; }
    return h ? h : 1;
}

/* Open addressing with linear probing; removed slots keep a NULL key and a nonzero hash (tombstones). */
static size_t slot(const gfx_map *map, const void *key, size_t length, uint64_t hash, bool *found) {
    size_t mask = map->capacity - 1, index = (size_t)hash & mask, tomb = SIZE_MAX;
    for (;;) {
        gfx_map_entry *e = &map->entries[index];
        if (!e->key) {
            if (!e->hash) { *found = false; return tomb != SIZE_MAX ? tomb : index; }
            if (tomb == SIZE_MAX) tomb = index;
        } else if (e->hash == hash && e->length == length && memcmp(e->key, key, length) == 0) {
            *found = true;
            return index;
        }
        index = (index + 1) & mask;
    }
}

static void rehash(gfx_map *map, size_t capacity) {
    gfx_map old = *map;
    map->entries = calloc(capacity, sizeof *map->entries);
    if (!map->entries) abort();
    map->capacity = capacity; map->count = 0; map->used = 0;
    for (size_t i = 0; i < old.capacity; i++) {
        gfx_map_entry *e = &old.entries[i];
        if (!e->key) continue;
        bool found;
        size_t index = slot(map, e->key, e->length, e->hash, &found);
        map->entries[index] = *e;
        map->count++; map->used++;
    }
    free(old.entries);
}

void *gfx_map_get(const gfx_map *map, const void *key, size_t length) {
    if (!map->count) return NULL;
    bool found;
    size_t index = slot(map, key, length, gfx_hash(key, length, 0), &found);
    return found ? map->entries[index].value : NULL;
}

void gfx_map_put(gfx_map *map, const void *key, size_t length, void *value) {
    if (!map->capacity || (map->used + 1) * 4 > map->capacity * 3) rehash(map, map->capacity ? (map->count * 2 > map->capacity ? map->capacity * 2 : map->capacity) : 64);
    uint64_t hash = gfx_hash(key, length, 0);
    bool found;
    size_t index = slot(map, key, length, hash, &found);
    gfx_map_entry *e = &map->entries[index];
    if (found) { e->value = value; return; }
    if (!e->hash) map->used++;
    e->key = malloc(length ? length : 1);
    if (!e->key) abort();
    memcpy(e->key, key, length);
    e->length = length; e->hash = hash; e->value = value;
    map->count++;
}

void *gfx_map_remove(gfx_map *map, const void *key, size_t length) {
    if (!map->count) return NULL;
    bool found;
    size_t index = slot(map, key, length, gfx_hash(key, length, 0), &found);
    if (!found) return NULL;
    gfx_map_entry *e = &map->entries[index];
    void *value = e->value;
    free(e->key);
    e->key = NULL; e->value = NULL; /* hash stays: a tombstone */
    map->count--;
    return value;
}

void gfx_map_each(const gfx_map *map, void (*visit)(const gfx_map_entry *entry, void *data), void *data) {
    for (size_t i = 0; i < map->capacity; i++) if (map->entries[i].key) visit(&map->entries[i], data);
}

void gfx_map_clear(gfx_map *map) {
    for (size_t i = 0; i < map->capacity; i++) free(map->entries[i].key);
    free(map->entries);
    memset(map, 0, sizeof *map);
}
