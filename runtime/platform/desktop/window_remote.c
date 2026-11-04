/* Remote windows (aqua/remote_window.h): every window of a process started with AQUARIUS_REMOTE_WINDOW=host:port (and
   AQUARIUS_REMOTE_WINDOW_TOKEN, the viewer's secret) is shown by a viewer over one TCP connection. Frames go out as the
   tiles that changed since the last frame sent, QOI-compressed; at most AQUA_REMOTE_WINDOW_IN_FLIGHT frames wait for
   the viewer's acknowledgement, and while it catches up only the latest frame is kept, so a slow viewer never slows
   the application down or sees an old picture last. Input comes back as aqua_window_event records. The runtime's
   loop waits on the socket (and a loopback wake socket other threads write to) while remote windows are open. */
#if !defined(_WIN32) && !defined(_GNU_SOURCE)
#define _GNU_SOURCE
#endif
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
typedef SOCKET socket_t;
#define BAD_SOCKET INVALID_SOCKET
#define poll_sockets WSAPoll
#define close_socket closesocket
#define SEND_FLAGS 0
static bool would_block(void) { return WSAGetLastError() == WSAEWOULDBLOCK; }
#else
#include <errno.h>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>
typedef int socket_t;
#define BAD_SOCKET (-1)
#define poll_sockets poll
#define close_socket close
#if defined(MSG_NOSIGNAL)
#define SEND_FLAGS MSG_NOSIGNAL   /* a viewer that went away is a lost connection, not SIGPIPE */
#else
#define SEND_FLAGS 0              /* systems without it set SO_NOSIGPIPE on the socket instead */
#endif
static bool would_block(void) { return errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR; }
#endif
#define GLFW_INCLUDE_NONE
#include <GLFW/glfw3.h>
#include <aqua/remote_window.h>
#include <stdio.h>
#include "desktop.h"
#include "window_remote.h"

struct remote_window {
    aqua_window *window;
    uint32_t id;
    int32_t width, height;             /* logical units, as the viewer shows the window */
    float scale;
    uint32_t flags;
    bool should_close, focused, text_enabled;
    int32_t text_rect[4];
    uint8_t keys[GLFW_KEY_LAST + 1], buttons[GLFW_MOUSE_BUTTON_LAST + 1];
    double cursor_x, cursor_y;
    /* The last frame sent (what the viewer shows), the newest frame not sent yet, frames awaiting acknowledgement. */
    uint8_t *sent; int32_t sent_width, sent_height;
    uint8_t *pending; int32_t pending_width, pending_height; bool has_pending;
    uint32_t number, acknowledged;
    remote_window *next;
};

static struct {
    bool checked, enabled, broken;
    socket_t socket, wake;
    aqua_remote_buffer out;            /* bytes not written yet */
    uint8_t *in; size_t in_length, in_capacity;
    remote_window *windows;
    uint32_t next_id;
    char *paste;
} viewer = {.socket = BAD_SOCKET, .wake = BAD_SOCKET};

static void set_nonblocking(socket_t s) {
#if defined(_WIN32)
    u_long on = 1;
    ioctlsocket(s, FIONBIO, &on);
#else
    fcntl(s, F_SETFL, fcntl(s, F_GETFL, 0) | O_NONBLOCK);
#endif
}

bool remote_enabled(void) {
    if (!viewer.checked) {
        viewer.checked = true;
        const char *address = getenv("AQUARIUS_REMOTE_WINDOW");
        viewer.enabled = address && *address;
    }
    return viewer.enabled;
}

double remote_time(void) {
#if defined(_WIN32)
    static LARGE_INTEGER frequency, start;
    LARGE_INTEGER now;
    if (!frequency.QuadPart) { QueryPerformanceFrequency(&frequency); QueryPerformanceCounter(&start); }
    QueryPerformanceCounter(&now);
    return (double)(now.QuadPart - start.QuadPart) / (double)frequency.QuadPart;
#else
    static struct timespec start;
    struct timespec now;
    if (!start.tv_sec && !start.tv_nsec) clock_gettime(CLOCK_MONOTONIC, &start);
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (double)(now.tv_sec - start.tv_sec) + (double)(now.tv_nsec - start.tv_nsec) / 1e9;
#endif
}

/* ---- The connection ---- */

/* Writes what the socket takes now; with `wait`, keeps writing (waiting for the socket, up to two seconds) until
   everything is out. */
static void flush(bool wait) {
    size_t written = 0;
    double deadline = remote_time() + 2;
    while (!viewer.broken && written < viewer.out.length) {
        int chunk = (int)(viewer.out.length - written > (1u << 30) ? (1u << 30) : viewer.out.length - written);
        int n = (int)send(viewer.socket, (const char *)viewer.out.data + written, chunk, SEND_FLAGS);
        if (n > 0) { written += (size_t)n; continue; }
        if (n < 0 && would_block()) {
            if (!wait || remote_time() > deadline) break;
            struct pollfd p = {.fd = viewer.socket, .events = POLLOUT};
            poll_sockets(&p, 1, 100);
            continue;
        }
        viewer.broken = true;
    }
    if (written) {
        memmove(viewer.out.data, viewer.out.data + written, viewer.out.length - written);
        viewer.out.length -= written;
    }
}

static bool connect_viewer(char **error) {
    if (viewer.socket != BAD_SOCKET) return !viewer.broken;
#if defined(_WIN32)
    WSADATA data;
    WSAStartup(MAKEWORD(2, 2), &data);
#endif
    char host[256];
    snprintf(host, sizeof host, "%s", getenv("AQUARIUS_REMOTE_WINDOW"));
    char *colon = strrchr(host, ':');
    if (!colon) { *error = strdup("AQUARIUS_REMOTE_WINDOW must be host:port."); return false; }
    *colon = 0;
    struct addrinfo hints = {0}, *found = NULL;
    hints.ai_family = AF_UNSPEC; hints.ai_socktype = SOCK_STREAM;
    if (getaddrinfo(host, colon + 1, &hints, &found) != 0 || !found) { *error = strdup("The remote window viewer's address cannot be resolved."); return false; }
    socket_t s = BAD_SOCKET;
    for (struct addrinfo *a = found; a && s == BAD_SOCKET; a = a->ai_next) {
        s = socket(a->ai_family, a->ai_socktype, a->ai_protocol);
        if (s != BAD_SOCKET && connect(s, a->ai_addr, (int)a->ai_addrlen) != 0) { close_socket(s); s = BAD_SOCKET; }
    }
    freeaddrinfo(found);
    if (s == BAD_SOCKET) { *error = strdup("The remote window viewer is unreachable."); return false; }
    int on = 1;
    setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (const char *)&on, sizeof on);
#if defined(SO_NOSIGPIPE)
    setsockopt(s, SOL_SOCKET, SO_NOSIGPIPE, &on, sizeof on);
#endif
    set_nonblocking(s);
    viewer.socket = s;
    /* The wake socket: a loopback UDP socket connected to itself, which other threads write a byte to. */
    socket_t w = socket(AF_INET, SOCK_DGRAM, 0);
    struct sockaddr_in self = {0};
    self.sin_family = AF_INET; self.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    socklen_t size = sizeof self;
    if (w != BAD_SOCKET && bind(w, (struct sockaddr *)&self, sizeof self) == 0 && getsockname(w, (struct sockaddr *)&self, &size) == 0 &&
        connect(w, (struct sockaddr *)&self, sizeof self) == 0) {
        set_nonblocking(w);
        viewer.wake = w;
    } else if (w != BAD_SOCKET) close_socket(w);
    const char *token = getenv("AQUARIUS_REMOTE_WINDOW_TOKEN");
    size_t at = aqua_remote_begin(&viewer.out, AQUA_REMOTE_HELLO);
    aqua_remote_u32(&viewer.out, AQUA_REMOTE_WINDOW_MAGIC);
    aqua_remote_u16(&viewer.out, AQUA_REMOTE_WINDOW_VERSION);
    aqua_remote_string(&viewer.out, token ? token : "");
    aqua_remote_end(&viewer.out, at);
    return true;
}

/* The viewer went away: every window is asked to close, as if its user had closed it. */
static void lose_viewer(void) {
    if (viewer.broken && !viewer.windows) return;
    viewer.broken = true;
    for (remote_window *r = viewer.windows; r; r = r->next) {
        if (r->should_close) continue;
        r->should_close = true;
        aqua_window_event e = {AQUA_EVENT_CLOSE};
        desktop_window_emit(r->window, &e);
    }
}

/* ---- Loop integration: wait on the socket and the wake socket ---- */

static bool wait_events(double timeout_ms) {
    if (!viewer.windows) return false;
    struct pollfd p[2] = {{.fd = viewer.socket, .events = POLLIN}, {.fd = viewer.wake, .events = POLLIN}};
    if (viewer.out.length) p[0].events |= POLLOUT;
    int count = viewer.wake != BAD_SOCKET ? 2 : 1;
    if (viewer.broken) count = 0;
    int timeout = timeout_ms >= 1e8 ? -1 : timeout_ms <= 0 ? 0 : (int)(timeout_ms + 0.999);
    if (viewer.wake == BAD_SOCKET && (timeout < 0 || timeout > 10)) timeout = 10;
    if (count) poll_sockets(p, (unsigned)count, timeout);
#if defined(_WIN32)
    else Sleep((DWORD)(timeout < 0 ? 10 : timeout));
#else
    else if (timeout) { struct timespec t = {timeout / 1000, (long)(timeout % 1000) * 1000000L}; nanosleep(&t, NULL); }
#endif
    if (count > 1 && p[1].revents) { char drain[64]; while (recv(viewer.wake, drain, sizeof drain, 0) > 0) {} }
    if (p[0].revents & POLLOUT) flush(false);
    return true;
}
static void wake_events(void) { if (viewer.wake != BAD_SOCKET) send(viewer.wake, "w", 1, SEND_FLAGS); }

/* ---- Windows ---- */

static void send_text_state(remote_window *r) {
    size_t at = aqua_remote_begin(&viewer.out, AQUA_REMOTE_TEXT);
    aqua_remote_u32(&viewer.out, r->id);
    aqua_remote_u8(&viewer.out, r->text_enabled);
    for (int i = 0; i < 4; i++) aqua_remote_i32(&viewer.out, r->text_rect[i]);
    aqua_remote_end(&viewer.out, at);
}

remote_window *remote_open(aqua_window *window, int32_t width, int32_t height, const char *title, uint32_t flags, char **error) {
    /* What the program printed before the window opened reaches its reader first, so a server that reads both (a
       notebook attributing windows to the cell that printed last) sees them in order. */
    platform_flush();
    if (!connect_viewer(error)) return NULL;
    remote_window *r = calloc(1, sizeof *r);
    r->window = window;
    r->id = ++viewer.next_id;
    r->width = width > 0 ? width : 1; r->height = height > 0 ? height : 1;
    r->scale = 1;
    r->flags = flags;
    r->focused = true;
    r->next = viewer.windows;
    viewer.windows = r;
    size_t at = aqua_remote_begin(&viewer.out, AQUA_REMOTE_OPEN);
    aqua_remote_u32(&viewer.out, r->id);
    aqua_remote_i32(&viewer.out, r->width);
    aqua_remote_i32(&viewer.out, r->height);
    aqua_remote_u32(&viewer.out, flags);
    aqua_remote_string(&viewer.out, title);
    aqua_remote_end(&viewer.out, at);
    flush(false);
    desktop_set_window_loop(wait_events, wake_events);
    return r;
}

void remote_close(remote_window *r) {
    if (!r) return;
    for (remote_window **p = &viewer.windows; *p; p = &(*p)->next) if (*p == r) { *p = r->next; break; }
    size_t at = aqua_remote_begin(&viewer.out, AQUA_REMOTE_CLOSE);
    aqua_remote_u32(&viewer.out, r->id);
    aqua_remote_end(&viewer.out, at);
    flush(!viewer.windows);
    free(r->sent);
    free(r->pending);
    free(r);
    if (!viewer.windows) desktop_set_window_loop(NULL, NULL);
}

void remote_size(remote_window *r, int32_t *width, int32_t *height) { *width = r->width; *height = r->height; }
void remote_framebuffer_size(remote_window *r, int32_t *width, int32_t *height) {
    *width = (int32_t)(r->width * r->scale + 0.5f); *height = (int32_t)(r->height * r->scale + 0.5f);
}
float remote_scale(remote_window *r) { return r->scale; }
uint32_t remote_id(remote_window *r) { return r->id; }
bool remote_should_close(remote_window *r) { return r->should_close; }
void remote_set_should_close(remote_window *r, bool close) { r->should_close = close; }
bool remote_focused(remote_window *r) { return r->focused; }
int32_t remote_key(remote_window *r, int32_t key) { return key >= 0 && key <= GLFW_KEY_LAST ? r->keys[key] : GLFW_RELEASE; }
int32_t remote_mouse_button(remote_window *r, int32_t button) { return button >= 0 && button <= GLFW_MOUSE_BUTTON_LAST ? r->buttons[button] : GLFW_RELEASE; }
void remote_cursor(remote_window *r, double *x, double *y) { *x = r->cursor_x; *y = r->cursor_y; }

void remote_set_size(remote_window *r, int32_t width, int32_t height) {
    if (width <= 0 || height <= 0) return;
    r->width = width; r->height = height;
    size_t at = aqua_remote_begin(&viewer.out, AQUA_REMOTE_SIZE);
    aqua_remote_u32(&viewer.out, r->id);
    aqua_remote_i32(&viewer.out, width);
    aqua_remote_i32(&viewer.out, height);
    aqua_remote_end(&viewer.out, at);
    flush(false);
    desktop_window_resized(r->window);
    aqua_window_event e = {AQUA_EVENT_RESIZE}; e.x = width; e.y = height;
    desktop_window_emit(r->window, &e);
}

void remote_set_title(remote_window *r, const char *title) {
    size_t at = aqua_remote_begin(&viewer.out, AQUA_REMOTE_TITLE);
    aqua_remote_u32(&viewer.out, r->id);
    aqua_remote_string(&viewer.out, title);
    aqua_remote_end(&viewer.out, at);
    flush(false);
}

void remote_set_cursor(remote_window *r, int32_t shape) {
    size_t at = aqua_remote_begin(&viewer.out, AQUA_REMOTE_CURSOR);
    aqua_remote_u32(&viewer.out, r->id);
    aqua_remote_i32(&viewer.out, shape);
    aqua_remote_end(&viewer.out, at);
    flush(false);
}

void remote_text_input(remote_window *r, bool enabled) {
    if (enabled == r->text_enabled) return;
    r->text_enabled = enabled;
    send_text_state(r);
    flush(false);
}
void remote_text_rect(remote_window *r, int32_t x, int32_t y, int32_t width, int32_t height) {
    if (r->text_rect[0] == x && r->text_rect[1] == y && r->text_rect[2] == width && r->text_rect[3] == height) return;
    r->text_rect[0] = x; r->text_rect[1] = y; r->text_rect[2] = width; r->text_rect[3] = height;
    send_text_state(r);
    flush(false);
}

/* ---- Frames ---- */

/* Sends `rgba` (tightly packed) as the window's next frame: the tiles that differ from the last frame sent. */
static void send_frame(remote_window *r, const uint8_t *rgba, int32_t width, int32_t height) {
    bool resized = !r->sent || r->sent_width != width || r->sent_height != height;
    if (!aqua_remote_frame(&viewer.out, r->id, r->number + 1, rgba, width, height, r->scale, resized ? NULL : r->sent)) return;
    r->number++;
    size_t bytes = (size_t)width * (size_t)height * 4;
    if (resized) { free(r->sent); r->sent = malloc(bytes); r->sent_width = width; r->sent_height = height; }
    if (r->sent) memcpy(r->sent, rgba, bytes);
    flush(false);
}

static void send_pending(remote_window *r) {
    if (!r->has_pending || r->number - r->acknowledged >= AQUA_REMOTE_WINDOW_IN_FLIGHT) return;
    r->has_pending = false;
    send_frame(r, r->pending, r->pending_width, r->pending_height);
}

bool remote_present(remote_window *r, const uint8_t *rgba, int32_t width, int32_t height, int32_t stride) {
    if (width <= 0 || height <= 0 || !rgba || viewer.broken) return !viewer.broken;
    /* Every frame is kept as the pending one; it goes out at once unless the viewer is behind. */
    size_t row = (size_t)width * 4, bytes = row * (size_t)height;
    if (!r->pending || r->pending_width != width || r->pending_height != height) {
        free(r->pending);
        r->pending = malloc(bytes);
        if (!r->pending) return false;
        r->pending_width = width; r->pending_height = height;
    }
    if ((size_t)stride == row) memcpy(r->pending, rgba, bytes);
    else for (int32_t y = 0; y < height; y++) memcpy(r->pending + (size_t)y * row, rgba + (size_t)y * (size_t)stride, row);
    /* A window is opaque on a screen (its surface's alpha is not composited): the same for the viewer. */
    for (size_t i = 3; i < bytes; i += 4) r->pending[i] = 255;
    r->has_pending = true;
    send_pending(r);
    return true;
}

/* ---- The viewer's messages ---- */

static remote_window *find(uint32_t id) {
    for (remote_window *r = viewer.windows; r; r = r->next) if (r->id == id) return r;
    return NULL;
}

static void receive_event(aqua_remote_reader *m) {
    remote_window *r = find(aqua_remote_read_u32(m));
    aqua_window_event e = {0};
    e.type = aqua_remote_read_i32(m);
    e.key = aqua_remote_read_i32(m); e.scancode = aqua_remote_read_i32(m); e.action = aqua_remote_read_i32(m);
    e.mods = aqua_remote_read_i32(m); e.button = aqua_remote_read_i32(m);
    e.x = aqua_remote_read_f64(m); e.y = aqua_remote_read_f64(m);
    e.pointer = aqua_remote_read_u32(m); e.pointer_type = aqua_remote_read_i32(m);
    e.pressure = aqua_remote_read_f32(m); e.tilt_x = aqua_remote_read_f32(m); e.tilt_y = aqua_remote_read_f32(m);
    e.cursor = aqua_remote_read_i32(m); e.index = aqua_remote_read_i32(m); e.count = aqua_remote_read_i32(m);
    char *text = aqua_remote_read_string(m);
    if (!r || m->failed) { free(text); return; }
    e.text = text;
    switch (e.type) {
    case AQUA_EVENT_KEY: if (e.key >= 0 && e.key <= GLFW_KEY_LAST) r->keys[e.key] = (uint8_t)(e.action != GLFW_RELEASE); break;
    case AQUA_EVENT_BUTTON:
        if (e.button >= 0 && e.button <= GLFW_MOUSE_BUTTON_LAST) r->buttons[e.button] = (uint8_t)(e.action != GLFW_RELEASE);
        r->cursor_x = e.x; r->cursor_y = e.y;
        break;
    case AQUA_EVENT_POINTER: r->cursor_x = e.x; r->cursor_y = e.y; break;
    case AQUA_EVENT_FOCUS:
        r->focused = e.action != 0;
        if (!r->focused) { memset(r->keys, 0, sizeof r->keys); memset(r->buttons, 0, sizeof r->buttons); }
        break;
    case AQUA_EVENT_CLOSE: r->should_close = true; break;
    case AQUA_EVENT_RESIZE: case AQUA_EVENT_DESTROYED: free(text); return;   /* sizes come in VIEW; destruction is ours */
    default: break;
    }
    desktop_window_emit(r->window, &e);
    free(text);
}

static void receive_view(aqua_remote_reader *m) {
    remote_window *r = find(aqua_remote_read_u32(m));
    int32_t width = aqua_remote_read_i32(m), height = aqua_remote_read_i32(m);
    float scale = aqua_remote_read_f32(m);
    if (!r || m->failed || width <= 0 || height <= 0 || width > 16384 || height > 16384) return;
    if (!(scale >= 0.25f && scale <= 8)) scale = 1;
    /* A VIEW is also how a viewer attaches (again): it has none of the earlier frames, so nothing it never saw waits
       for its acknowledgement and the next frame is whole. The last frame goes out again unless a newer one waits. */
    r->acknowledged = r->number;
    if (r->sent) {
        if (!r->has_pending) {
            free(r->pending);
            r->pending = r->sent;
            r->pending_width = r->sent_width; r->pending_height = r->sent_height;
            r->has_pending = true;
        } else free(r->sent);
        r->sent = NULL;
    }
    send_pending(r);
    if (width == r->width && height == r->height && scale == r->scale) return;
    r->width = width; r->height = height; r->scale = scale;
    desktop_window_resized(r->window);
    aqua_window_event e = {AQUA_EVENT_RESIZE}; e.x = width; e.y = height;
    desktop_window_emit(r->window, &e);
}

static void receive(uint8_t type, aqua_remote_reader *m) {
    switch (type) {
    case AQUA_REMOTE_EVENT: receive_event(m); break;
    case AQUA_REMOTE_VIEW: receive_view(m); break;
    case AQUA_REMOTE_ACK: {
        remote_window *r = find(aqua_remote_read_u32(m));
        uint32_t number = aqua_remote_read_u32(m);
        if (r && !m->failed && number <= r->number && number > r->acknowledged) { r->acknowledged = number; send_pending(r); }
        break;
    }
    case AQUA_REMOTE_PASTE: {
        char *text = aqua_remote_read_string(m);
        if (text) { free(viewer.paste); viewer.paste = text; }
        break;
    }
    default: break;   /* newer viewers' messages this sender does not know */
    }
}

void remote_poll(void) {
    if (viewer.socket == BAD_SOCKET || viewer.broken) { if (viewer.broken) lose_viewer(); return; }
    flush(false);
    for (;;) {
        if (viewer.in_capacity - viewer.in_length < 65536) {
            size_t capacity = viewer.in_capacity ? viewer.in_capacity * 2 : 131072;
            uint8_t *grown = realloc(viewer.in, capacity);
            if (!grown) { lose_viewer(); return; }
            viewer.in = grown; viewer.in_capacity = capacity;
        }
        int n = (int)recv(viewer.socket, (char *)viewer.in + viewer.in_length, (int)(viewer.in_capacity - viewer.in_length), 0);
        if (n > 0) { viewer.in_length += (size_t)n; continue; }
        if (n < 0 && would_block()) break;
        lose_viewer();   /* closed, or failed */
        return;
    }
    size_t used = 0;
    for (;;) {
        uint8_t type;
        aqua_remote_reader m;
        long n = aqua_remote_next(viewer.in + used, viewer.in_length - used, &type, &m);
        if (n < 0) { lose_viewer(); return; }
        if (n == 0) break;
        used += (size_t)n;
        receive(type, &m);
    }
    memmove(viewer.in, viewer.in + used, viewer.in_length - used);
    viewer.in_length -= used;
    flush(false);
    if (viewer.broken) lose_viewer();
}

char *remote_clipboard_text(void) { return viewer.paste ? strdup(viewer.paste) : NULL; }
bool remote_clipboard_set(const char *utf8) {
    if (viewer.socket == BAD_SOCKET || viewer.broken) return false;
    free(viewer.paste);
    viewer.paste = strdup(utf8 ? utf8 : "");
    size_t at = aqua_remote_begin(&viewer.out, AQUA_REMOTE_CLIPBOARD);
    aqua_remote_string(&viewer.out, utf8);
    aqua_remote_end(&viewer.out, at);
    flush(false);
    return true;
}
