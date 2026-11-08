/* aqua/window.h on iOS and iPadOS, and the process entry: UIKit owns the main thread, so main() starts
   UIApplicationMain and the application delegate runs the runtime on a thread of its own (platform.c ios_run). The
   screen is the application's one window: a full-screen view whose layer is a CAMetalLayer (AQUA_SURFACE_METAL, which
   WebGPU renders into from the runtime thread). Touches become pointer and button events in points (logical units),
   hardware keyboard keys GLFW-coded key events, and the soft keyboard's text TEXT events; UIKit's events are queued on
   the main thread and dispatched on the runtime thread (ios_dispatch_events). Like the Android activity shim, this file
   only forwards what iOS delivers to Objective-C alone. */
#import <UIKit/UIKit.h>
#import <QuartzCore/CAMetalLayer.h>
#include <pthread.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include "../../host/host.h"
#include "ios.h"

typedef struct listener { aqua_window_listener run; void *data; } listener;
struct aqua_window {
    aqua_runtime *rt;
    listener *listeners; uint32_t listener_count, listener_capacity;
    bool should_close, text_enabled, focused;
    double pointer_x, pointer_y;
    uint8_t keys[512];
    int32_t buttons;
};

static aqua_window *window;            /* the one window, once a library opened it (runtime thread) */
static CAMetalLayer *metal_layer;      /* the view's layer, set on the main thread at launch */
static UIView *main_view;
static double point_scale = 1;         /* points to pixels */
static int32_t view_width, view_height; /* in points */

static double seconds(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (double)t.tv_sec + t.tv_nsec / 1e9; }

/* ---- Events: queued by the main thread, dispatched by the runtime thread ---- */
typedef struct queued { aqua_window_event event; char *text; } queued;
static queued *queue;
static size_t queue_length, queue_capacity;
static pthread_mutex_t queue_lock = PTHREAD_MUTEX_INITIALIZER;

static void post(aqua_window_event e, const char *text) {
    e.time = seconds();
    pthread_mutex_lock(&queue_lock);
    if (queue_length == queue_capacity) {
        queue_capacity = queue_capacity ? queue_capacity * 2 : 64;
        queue = realloc(queue, queue_capacity * sizeof *queue);
    }
    queue[queue_length++] = (queued){e, text ? strdup(text) : NULL};
    pthread_mutex_unlock(&queue_lock);
    if (window) platform_wake(window->rt);
}

bool ios_events_pending(void) {
    pthread_mutex_lock(&queue_lock);
    bool pending = queue_length > 0;
    pthread_mutex_unlock(&queue_lock);
    return pending;
}

static void emit(aqua_window_event *e) {
    if (!window) return;
    uint32_t n = window->listener_count;
    listener copy[16];
    listener *list = n <= 16 ? copy : malloc(n * sizeof *list);
    memcpy(list, window->listeners, n * sizeof *list);
    for (uint32_t i = 0; i < n; i++) list[i].run(window, e, list[i].data);
    if (list != copy) free(list);
}

void ios_dispatch_events(void) {
    pthread_mutex_lock(&queue_lock);
    queued *taken = queue;
    size_t count = queue_length;
    queue = NULL; queue_length = queue_capacity = 0;
    pthread_mutex_unlock(&queue_lock);
    for (size_t i = 0; i < count; i++) {
        aqua_window_event *e = &taken[i].event;
        e->text = taken[i].text;
        if (window) {
            if (e->type == AQUA_EVENT_POINTER && e->pointer == 0) { window->pointer_x = e->x; window->pointer_y = e->y; }
            if (e->type == AQUA_EVENT_BUTTON && e->pointer == 0) window->buttons = e->action ? 1 : 0;
            if (e->type == AQUA_EVENT_KEY && e->key >= 0 && e->key < 512) window->keys[e->key] = e->action ? 1 : 0;
            if (e->type == AQUA_EVENT_FOCUS) window->focused = e->action != 0;
            emit(e);
        }
        free(taken[i].text);
    }
    free(taken);
}

/* UIKit's HID usages (UIKeyboardHIDUsage) to GLFW key codes, for hardware keyboards. */
static int32_t glfw_key(long usage) {
    if (usage >= 0x04 && usage <= 0x1d) return 65 + (int32_t)(usage - 0x04);            /* A-Z */
    if (usage >= 0x1e && usage <= 0x26) return 49 + (int32_t)(usage - 0x1e);            /* 1-9 */
    if (usage >= 0x3a && usage <= 0x45) return 290 + (int32_t)(usage - 0x3a);           /* F1-F12 */
    switch (usage) {
    case 0x27: return 48;  case 0x28: return 257; case 0x29: return 256; case 0x2a: return 259; case 0x2b: return 258;
    case 0x2c: return 32;  case 0x2d: return 45;  case 0x2e: return 61;  case 0x2f: return 91;  case 0x30: return 93;
    case 0x31: return 92;  case 0x33: return 59;  case 0x34: return 39;  case 0x35: return 96;  case 0x36: return 44;
    case 0x37: return 46;  case 0x38: return 47;  case 0x39: return 280; case 0x4a: return 268; case 0x4b: return 266;
    case 0x4c: return 261; case 0x4d: return 269; case 0x4e: return 267; case 0x4f: return 262; case 0x50: return 263;
    case 0x51: return 264; case 0x52: return 265; case 0xe0: return 341; case 0xe1: return 340; case 0xe2: return 342;
    case 0xe3: return 343; case 0xe4: return 345; case 0xe5: return 344; case 0xe6: return 346; case 0xe7: return 347;
    default: return -1;
    }
}
static int32_t glfw_mods(UIKeyModifierFlags flags) {
    return (flags & UIKeyModifierShift ? 1 : 0) | (flags & UIKeyModifierControl ? 2 : 0) | (flags & UIKeyModifierAlternate ? 4 : 0) |
           (flags & UIKeyModifierCommand ? 8 : 0) | (flags & UIKeyModifierAlphaShift ? 16 : 0);
}

@interface AquaView : UIView <UIKeyInput>
@end

@implementation AquaView
+ (Class)layerClass { return [CAMetalLayer class]; }
- (BOOL)canBecomeFirstResponder { return YES; }
- (BOOL)hasText { return NO; }
- (void)insertText:(NSString *)text {
    aqua_window_event e = {AQUA_EVENT_TEXT};
    post(e, text.UTF8String);
}
- (void)deleteBackward {
    aqua_window_event press = {AQUA_EVENT_KEY}; press.key = 259; press.action = 1;
    aqua_window_event release = press; release.action = 0;
    post(press, NULL); post(release, NULL);
}
- (void)layoutSubviews {
    [super layoutSubviews];
    point_scale = self.window.screen.nativeScale > 0 ? self.window.screen.nativeScale : UIScreen.mainScreen.nativeScale;
    CAMetalLayer *layer = (CAMetalLayer *)self.layer;
    layer.contentsScale = point_scale;
    layer.drawableSize = CGSizeMake(self.bounds.size.width * point_scale, self.bounds.size.height * point_scale);
    view_width = (int32_t)self.bounds.size.width; view_height = (int32_t)self.bounds.size.height;
    aqua_window_event e = {AQUA_EVENT_RESIZE};
    e.x = view_width; e.y = view_height;
    post(e, NULL);
}
/* Each finger keeps one pointer id while it touches: the first free slot, so a lone finger is pointer 0, as a mouse is. */
static void *touch_slots[16];
static uint32_t touch_id(UITouch *touch, bool begin) {
    void *key = (__bridge void *)touch;
    for (uint32_t i = 0; i < 16; i++) if (touch_slots[i] == key) return i;
    if (begin) for (uint32_t i = 0; i < 16; i++) if (!touch_slots[i]) { touch_slots[i] = key; return i; }
    return 15;
}
- (void)touches:(NSSet<UITouch *> *)touches phase:(int)phase {   /* 0 began, 1 moved, 2 ended or cancelled */
    for (UITouch *touch in touches) {
        CGPoint p = [touch locationInView:self];
        aqua_window_event e = {AQUA_EVENT_POINTER};
        e.pointer = touch_id(touch, phase == 0);
        e.pointer_type = touch.type == UITouchTypePencil ? 2 : 1;
        e.x = p.x; e.y = p.y;
        e.pressure = touch.maximumPossibleForce > 0 ? (float)(touch.force / touch.maximumPossibleForce) : (phase == 2 ? 0 : 1);
        post(e, NULL);
        if (phase != 1) {
            aqua_window_event b = e;
            b.type = AQUA_EVENT_BUTTON; b.button = 0; b.action = phase == 0 ? 1 : 0;
            post(b, NULL);
        }
        if (phase == 2 && e.pointer < 16) touch_slots[e.pointer] = NULL;
    }
}
- (void)touchesBegan:(NSSet<UITouch *> *)touches withEvent:(UIEvent *)event { [self touches:touches phase:0]; }
- (void)touchesMoved:(NSSet<UITouch *> *)touches withEvent:(UIEvent *)event { [self touches:touches phase:1]; }
- (void)touchesEnded:(NSSet<UITouch *> *)touches withEvent:(UIEvent *)event { [self touches:touches phase:2]; }
- (void)touchesCancelled:(NSSet<UITouch *> *)touches withEvent:(UIEvent *)event { [self touches:touches phase:2]; }
- (void)presses:(NSSet<UIPress *> *)presses action:(int32_t)action {
    for (UIPress *press in presses) {
        if (!press.key) continue;
        aqua_window_event e = {AQUA_EVENT_KEY};
        e.key = glfw_key((long)press.key.keyCode); e.scancode = (int32_t)press.key.keyCode; e.action = action;
        e.mods = glfw_mods(press.key.modifierFlags);
        if (e.key >= 0) post(e, NULL);
    }
}
- (void)pressesBegan:(NSSet<UIPress *> *)presses withEvent:(UIPressesEvent *)event { [self presses:presses action:1]; }
- (void)pressesEnded:(NSSet<UIPress *> *)presses withEvent:(UIPressesEvent *)event { [self presses:presses action:0]; }
- (void)pressesCancelled:(NSSet<UIPress *> *)presses withEvent:(UIPressesEvent *)event { [self presses:presses action:0]; }
@end

@interface AquaViewController : UIViewController
@end
@implementation AquaViewController
- (void)loadView { self.view = [[AquaView alloc] initWithFrame:UIScreen.mainScreen.bounds]; self.view.multipleTouchEnabled = YES; }
- (BOOL)prefersHomeIndicatorAutoHidden { return YES; }
@end

@interface AquaAppDelegate : UIResponder <UIApplicationDelegate>
@end
@interface AquaSceneDelegate : UIResponder <UIWindowSceneDelegate>
@property (strong, nonatomic) UIWindow *window;
@end

/* The runtime thread: a large stack, as the desktop's aquarius-aot reserves one (compiled code recurses natively). */
static void *run_application(void *unused) {
    (void)unused;
    @autoreleasepool {
        NSString *resources = NSBundle.mainBundle.bundlePath;
        NSString *data = [NSSearchPathForDirectoriesInDomains(NSApplicationSupportDirectory, NSUserDomainMask, YES) firstObject];
        [NSFileManager.defaultManager createDirectoryAtPath:data withIntermediateDirectories:YES attributes:nil error:nil];
        int code = ios_run(resources.fileSystemRepresentation, data.fileSystemRepresentation);
        /* A finished application leaves, as it does on the other targets (and the simulator's console sees the code). */
        exit(code);
    }
    return NULL;
}

@implementation AquaAppDelegate
- (BOOL)application:(UIApplication *)application didFinishLaunchingWithOptions:(NSDictionary *)options { return YES; }
- (UISceneConfiguration *)application:(UIApplication *)application configurationForConnectingSceneSession:(UISceneSession *)session
                              options:(UISceneConnectionOptions *)options {
    UISceneConfiguration *configuration = [[UISceneConfiguration alloc] initWithName:@"Default" sessionRole:session.role];
    configuration.delegateClass = [AquaSceneDelegate class];
    return configuration;
}
@end

/* UIKit's scene life cycle (required by current SDKs): the first scene shows the view and starts the runtime thread. */
@implementation AquaSceneDelegate
- (void)scene:(UIScene *)scene willConnectToSession:(UISceneSession *)session options:(UISceneConnectionOptions *)options {
    UIWindowScene *windowScene = (UIWindowScene *)scene;
    self.window = [[UIWindow alloc] initWithWindowScene:windowScene];
    self.window.rootViewController = [AquaViewController new];
    [self.window makeKeyAndVisible];
    if (main_view) return;
    main_view = self.window.rootViewController.view;
    metal_layer = (CAMetalLayer *)main_view.layer;
    point_scale = windowScene.screen.nativeScale;
    view_width = (int32_t)main_view.bounds.size.width; view_height = (int32_t)main_view.bounds.size.height;
    pthread_attr_t attributes;
    pthread_attr_init(&attributes);
    pthread_attr_setstacksize(&attributes, 64u << 20);
    pthread_t thread;
    if (pthread_create(&thread, &attributes, run_application, NULL) != 0) pthread_create(&thread, NULL, run_application, NULL);
    pthread_attr_destroy(&attributes);
}
- (void)sceneDidBecomeActive:(UIScene *)scene {
    aqua_window_event e = {AQUA_EVENT_LIFECYCLE}; e.action = 1; post(e, NULL);
    aqua_window_event f = {AQUA_EVENT_FOCUS}; f.action = 1; post(f, NULL);
}
- (void)sceneWillResignActive:(UIScene *)scene {
    aqua_window_event e = {AQUA_EVENT_LIFECYCLE}; e.action = 0; post(e, NULL);
    aqua_window_event f = {AQUA_EVENT_FOCUS}; f.action = 0; post(f, NULL);
}
@end

int main(int argc, char **argv) {
    @autoreleasepool { return UIApplicationMain(argc, argv, nil, NSStringFromClass([AquaAppDelegate class])); }
}

/* ---- aqua/window.h ---- */
void aqua_window_hint(int32_t hint, int32_t value) { (void)hint; (void)value; }

aqua_window *aqua_window_create(aqua_runtime *rt, int32_t width, int32_t height, const char *title, uint32_t flags, char **error) {
    (void)width; (void)height; (void)title;
    if (flags & AQUA_WINDOW_OPENGL) { if (error) *error = strdup("OpenGL windows are not available on iOS; use WGPU or Processing."); return NULL; }
    if (window) { if (error) *error = strdup("iOS applications have one window."); return NULL; }
    if (!metal_layer) { if (error) *error = strdup("The application has no screen yet."); return NULL; }
    window = calloc(1, sizeof *window);
    window->rt = rt; window->focused = true;
    return window;
}
void aqua_window_destroy(aqua_window *w) {
    if (!w) return;
    aqua_window_event e = {AQUA_EVENT_DESTROYED};
    emit(&e);
    if (w->text_enabled) aqua_window_text_input(w, false);
    free(w->listeners);
    if (w == window) window = NULL;
    free(w);
}
void ios_windows_close(aqua_runtime *rt) { if (window && window->rt == rt) aqua_window_destroy(window); }
aqua_runtime *aqua_window_runtime(aqua_window *w) { return w->rt; }
bool aqua_window_should_close(aqua_window *w) { return w->should_close; }
void aqua_window_set_should_close(aqua_window *w, bool close) { w->should_close = close; }
void aqua_window_size(aqua_window *w, int32_t *width, int32_t *height) { (void)w; *width = view_width; *height = view_height; }
void aqua_window_framebuffer_size(aqua_window *w, int32_t *width, int32_t *height) {
    (void)w;
    *width = (int32_t)(view_width * point_scale + 0.5); *height = (int32_t)(view_height * point_scale + 0.5);
}
float aqua_window_content_scale(aqua_window *w) { (void)w; return (float)point_scale; }
void aqua_window_set_size(aqua_window *w, int32_t width, int32_t height) { (void)w; (void)width; (void)height; }
void aqua_window_set_position(aqua_window *w, int32_t x, int32_t y) { (void)w; (void)x; (void)y; }
void aqua_window_set_title(aqua_window *w, const char *title) { (void)w; (void)title; }
bool aqua_window_set_fullscreen(aqua_window *w, bool fullscreen) { (void)w; return fullscreen; }
bool aqua_window_focused(aqua_window *w) { return w->focused; }
bool aqua_display_size(aqua_runtime *rt, int32_t *width, int32_t *height) {
    (void)rt;
    CGRect bounds = UIScreen.mainScreen.nativeBounds;
    *width = (int32_t)bounds.size.width; *height = (int32_t)bounds.size.height;
    return true;
}
void aqua_windows_poll(aqua_runtime *rt) { (void)rt; ios_dispatch_events(); }
double aqua_window_time(aqua_runtime *rt) { (void)rt; static double start; if (!start) start = seconds(); return seconds() - start; }
int32_t aqua_window_key(aqua_window *w, int32_t key) { return key >= 0 && key < 512 ? w->keys[key] : 0; }
int32_t aqua_window_mouse_button(aqua_window *w, int32_t button) { return button == 0 ? w->buttons : 0; }
void aqua_window_cursor(aqua_window *w, double *x, double *y) { *x = w->pointer_x; *y = w->pointer_y; }
void aqua_window_set_cursor_mode(aqua_window *w, int32_t mode) { (void)w; (void)mode; }
bool aqua_window_set_cursor_shape(aqua_window *w, int32_t shape) { (void)w; (void)shape; return false; }
bool aqua_window_set_cursor_image(aqua_window *w, int32_t width, int32_t height, const uint8_t *rgba, int32_t x, int32_t y) {
    (void)w; (void)width; (void)height; (void)rgba; (void)x; (void)y;
    return false;
}
bool aqua_window_capture_pointer(aqua_window *w, bool capture) { (void)w; (void)capture; return false; }
void aqua_window_listen(aqua_window *w, aqua_window_listener run, void *data) {
    if (w->listener_count == w->listener_capacity) {
        w->listener_capacity = w->listener_capacity ? w->listener_capacity * 2 : 4;
        w->listeners = realloc(w->listeners, w->listener_capacity * sizeof *w->listeners);
    }
    w->listeners[w->listener_count].run = run;
    w->listeners[w->listener_count++].data = data;
}
void aqua_window_unlisten(aqua_window *w, aqua_window_listener run, void *data) {
    for (uint32_t i = 0; i < w->listener_count; i++)
        if (w->listeners[i].run == run && w->listeners[i].data == data) {
            memmove(w->listeners + i, w->listeners + i + 1, (w->listener_count - i - 1) * sizeof *w->listeners);
            w->listener_count--;
            return;
        }
}
/* The soft keyboard: the view becomes the first responder (UIKeyInput), on the main thread. */
void aqua_window_text_input(aqua_window *w, bool enabled) {
    if (w->text_enabled == enabled) return;
    w->text_enabled = enabled;
    dispatch_async(dispatch_get_main_queue(), ^{ if (enabled) [main_view becomeFirstResponder]; else [main_view resignFirstResponder]; });
}
bool aqua_window_text_input_enabled(aqua_window *w) { return w && w->text_enabled; }
void aqua_window_text_rect(aqua_window *w, int32_t x, int32_t y, int32_t width, int32_t height) { (void)w; (void)x; (void)y; (void)width; (void)height; }
bool aqua_window_composition_supported(aqua_window *w) { (void)w; return false; }
bool aqua_window_surface(aqua_window *w, aqua_surface *s) {
    (void)w;
    memset(s, 0, sizeof *s);
    if (!metal_layer) return false;
    s->kind = AQUA_SURFACE_METAL; s->handle = (__bridge void *)metal_layer;
    return true;
}
void aqua_window_make_current(aqua_window *w) { (void)w; }
void aqua_window_swap(aqua_window *w) { (void)w; }
void aqua_window_swap_interval(aqua_runtime *rt, int32_t interval) { (void)rt; (void)interval; }
bool aqua_window_present(aqua_window *w, const uint8_t *rgba, int32_t width, int32_t height, int32_t stride) {
    (void)w; (void)rgba; (void)width; (void)height; (void)stride;
    return false;
}
void *aqua_gl_proc(const char *name) { (void)name; return NULL; }

/* The general pasteboard, read and written on the main thread as UIKit requires. */
char *aqua_clipboard_text(aqua_runtime *rt) {
    (void)rt;
    __block char *text = NULL;
    void (^read)(void) = ^{ NSString *s = UIPasteboard.generalPasteboard.string; if (s.length) text = strdup(s.UTF8String); };
    if (NSThread.isMainThread) read(); else dispatch_sync(dispatch_get_main_queue(), read);
    return text;
}
bool aqua_clipboard_set_text(aqua_runtime *rt, const char *utf8) {
    (void)rt;
    NSString *s = [NSString stringWithUTF8String:utf8 ? utf8 : ""];
    if (!s) return false;
    dispatch_async(dispatch_get_main_queue(), ^{ UIPasteboard.generalPasteboard.string = s; });
    return true;
}
void *aqua_window_native(aqua_window *w) { (void)w; return (__bridge void *)main_view; }
