/* WebFrame / 網頁框架: what src/webframe.c needs from the target. Frames are handles the platform layer gives out. */
#ifndef AQUA_WEBFRAME_H
#define AQUA_WEBFRAME_H

#include <aqua/aqua.h>

bool webframe_supported(void);                        /* whether this target has a page to embed frames into */
int32_t webframe_create(const char *url);             /* a new frame showing url, or 0 */
void webframe_place(int32_t frame, double x, double y, double width, double height); /* page units, from the canvas */
void webframe_navigate(int32_t frame, const char *url);
void webframe_reload(int32_t frame);
void webframe_show(int32_t frame, bool visible);
void webframe_remove(int32_t frame);

#endif
