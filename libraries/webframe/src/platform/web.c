/* Web: iframes through web-glue/frame. */
#include "../webframe.h"
#include <string.h>

AQUA_WEB_IMPORT(frame_create) int32_t web_frame_create(const char *url, uint32_t length);
AQUA_WEB_IMPORT(frame_place) void web_frame_place(int32_t frame, double x, double y, double width, double height);
AQUA_WEB_IMPORT(frame_navigate) void web_frame_navigate(int32_t frame, const char *url, uint32_t length);
AQUA_WEB_IMPORT(frame_reload) void web_frame_reload(int32_t frame);
AQUA_WEB_IMPORT(frame_show) void web_frame_show(int32_t frame, int32_t visible);
AQUA_WEB_IMPORT(frame_remove) void web_frame_remove(int32_t frame);

bool webframe_supported(void) { return true; }
int32_t webframe_create(const char *url) { return web_frame_create(url, (uint32_t)strlen(url)); }
void webframe_place(int32_t frame, double x, double y, double width, double height) { web_frame_place(frame, x, y, width, height); }
void webframe_navigate(int32_t frame, const char *url) { web_frame_navigate(frame, url, (uint32_t)strlen(url)); }
void webframe_reload(int32_t frame) { web_frame_reload(frame); }
void webframe_show(int32_t frame, bool visible) { web_frame_show(frame, visible ? 1 : 0); }
void webframe_remove(int32_t frame) { web_frame_remove(frame); }
