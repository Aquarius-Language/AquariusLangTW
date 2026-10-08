#define GLFW_INCLUDE_NONE
#include "text_input.h"
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#ifdef _WIN32
#define GLFW_EXPOSE_NATIVE_WIN32
#include <GLFW/glfw3native.h>
#include <imm.h>
#define AQUA __declspec(dllexport)
#else
#define AQUA __attribute__((visibility("default")))
#endif

enum { TEXT_COMMIT = 1, TEXT_START = 2, TEXT_UPDATE = 3, TEXT_END = 4 };
typedef struct TextEvent {
    struct TextEvent* next;
    int kind, cursor;
    size_t size;
    char text[1];
} TextEvent;
typedef struct TextInput {
    TextEvent *first, *last, *returned;
    size_t bytes;
    int enabled, composing, failed;
    GLFWcharfun character;
#ifdef _WIN32
    HWND hwnd;
    WNDPROC previous;
    RECT caret;
#endif
} TextInput;

static TextInput* state(GLFWwindow* window) { return (TextInput*)glfwGetWindowUserPointer(window); }
static void clear_events(TextInput* input) {
    while (input->first) {
        TextEvent* event = input->first;
        input->first = event->next;
        free(event);
    }
    free(input->returned);
    input->returned = input->last = NULL;
    input->bytes = 0;
    input->failed = 0;
}
static void enqueue(TextInput* input, int kind, const char* text, size_t length, int cursor) {
    size_t size = sizeof(TextEvent) + length;
    if (!input->enabled || input->failed) return;
    /* Report overflow to the caller instead of silently truncating typed text. */
    if (size > 1024 * 1024 || input->bytes > 1024 * 1024 - size) { input->failed = 1; return; }
    TextEvent* event = (TextEvent*)malloc(size);
    if (!event) { input->failed = 1; return; }
    event->next = NULL; event->kind = kind; event->cursor = cursor; event->size = size;
    memcpy(event->text, text, length); event->text[length] = 0;
    if (input->last) input->last->next = event; else input->first = event;
    input->last = event; input->bytes += size;
}
void aqua_text_character(GLFWwindow* window, unsigned int c) {
    TextInput* input = state(window);
    char utf8[4]; size_t length;
    if (!input || !c || c > 0x10ffff || (c >= 0xd800 && c <= 0xdfff)) return;
    if (c < 0x80) { utf8[0] = (char)c; length = 1; }
    else if (c < 0x800) { utf8[0] = (char)(0xc0 | (c >> 6)); utf8[1] = (char)(0x80 | (c & 63)); length = 2; }
    else if (c < 0x10000) { utf8[0] = (char)(0xe0 | (c >> 12)); utf8[1] = (char)(0x80 | ((c >> 6) & 63)); utf8[2] = (char)(0x80 | (c & 63)); length = 3; }
    else { utf8[0] = (char)(0xf0 | (c >> 18)); utf8[1] = (char)(0x80 | ((c >> 12) & 63)); utf8[2] = (char)(0x80 | ((c >> 6) & 63)); utf8[3] = (char)(0x80 | (c & 63)); length = 4; }
    enqueue(input, TEXT_COMMIT, utf8, length, 0);
}
static void end_composition(TextInput* input) {
    if (!input->composing) return;
    input->composing = 0;
    enqueue(input, TEXT_END, "", 0, 0);
}
#ifdef _WIN32
static const wchar_t property_name[] = L"Aquarius.TextInput";
static void position_ime(TextInput* input) {
    HIMC context = ImmGetContext(input->hwnd);
    if (!context) return;
    COMPOSITIONFORM composition = {0};
    composition.dwStyle = CFS_POINT;
    composition.ptCurrentPos.x = input->caret.left;
    composition.ptCurrentPos.y = input->caret.top;
    ImmSetCompositionWindow(context, &composition);
    CANDIDATEFORM candidate = {0};
    candidate.dwStyle = CFS_EXCLUDE;
    candidate.ptCurrentPos.x = input->caret.left;
    candidate.ptCurrentPos.y = input->caret.bottom;
    candidate.rcArea = input->caret;
    ImmSetCandidateWindow(context, &candidate);
    ImmReleaseContext(input->hwnd, context);
}
static void read_composition(TextInput* input, GLFWwindow* window, HIMC context, DWORD flag) {
    LONG bytes = ImmGetCompositionStringW(context, flag, NULL, 0);
    if (bytes < 0 || bytes > 1024 * 1024 || bytes % sizeof(wchar_t)) { input->failed = 1; return; }
    wchar_t* wide = (wchar_t*)calloc((size_t)bytes + sizeof(wchar_t), 1);
    if (!wide) { input->failed = 1; return; }
    if (bytes && ImmGetCompositionStringW(context, flag, wide, (DWORD)bytes) != bytes) { free(wide); input->failed = 1; return; }
    int count = bytes / sizeof(wchar_t);
    if (flag == GCS_RESULTSTR) {
        /* Handle the result here; do not let DefWindowProc generate it again. */
        for (int i = 0; i < count; i++) {
            unsigned int c = wide[i];
            if (c >= 0xd800 && c <= 0xdbff && i + 1 < count && wide[i+1] >= 0xdc00 && wide[i+1] <= 0xdfff)
                c = 0x10000 + ((c - 0xd800) << 10) + (wide[++i] - 0xdc00);
            else if (c >= 0xd800 && c <= 0xdfff) c = 0xfffd;
            input->character(window, c);
        }
    } else {
        LONG cursor = ImmGetCompositionStringW(context, GCS_CURSORPOS, NULL, 0);
        int length = count ? WideCharToMultiByte(CP_UTF8, 0, wide, count, NULL, 0, NULL, NULL) : 0;
        char* utf8 = (char*)malloc((size_t)length + 1);
        if (!utf8) input->failed = 1;
        else {
            if (length) WideCharToMultiByte(CP_UTF8, 0, wide, count, utf8, length, NULL, NULL);
            enqueue(input, TEXT_UPDATE, utf8, (size_t)length, cursor < 0 ? 0 : cursor > count ? count : cursor);
            free(utf8);
        }
    }
    free(wide);
}
static LRESULT CALLBACK text_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    GLFWwindow* window = (GLFWwindow*)GetPropW(hwnd, property_name);
    TextInput* input = state(window);
    if (message == WM_GETOBJECT) {
        extern intptr_t aqua_application_get_object(GLFWwindow* window, uintptr_t parameter, intptr_t object_id);
        intptr_t accessible = aqua_application_get_object(window, (uintptr_t)wparam, (intptr_t)lparam);
        if (accessible) return (LRESULT)accessible;
    }
    if (message == WM_CAPTURECHANGED) {
        extern void aqua_application_capture_lost(GLFWwindow* window);
        aqua_application_capture_lost(window);
    }
    if (input->enabled) {
        switch (message) {
        case WM_IME_SETCONTEXT:
            lparam &= ~ISC_SHOWUICOMPOSITIONWINDOW;
            break;
        case WM_IME_STARTCOMPOSITION:
            if (!input->composing) { input->composing = 1; enqueue(input, TEXT_START, "", 0, 0); }
            position_ime(input);
            return 0;
        case WM_IME_COMPOSITION: {
            HIMC context = ImmGetContext(hwnd);
            if (context) {
                if (lparam & GCS_RESULTSTR) read_composition(input, window, context, GCS_RESULTSTR);
                if (lparam & (GCS_COMPSTR | GCS_CURSORPOS)) {
                    if (!input->composing) { input->composing = 1; enqueue(input, TEXT_START, "", 0, 0); }
                    read_composition(input, window, context, GCS_COMPSTR);
                } else if (!lparam) enqueue(input, TEXT_UPDATE, "", 0, 0);
                ImmReleaseContext(hwnd, context);
            }
            return 0;
        }
        case WM_IME_ENDCOMPOSITION:
            end_composition(input); return 0;
        case WM_IME_CHAR:
            return 0;
        case WM_KILLFOCUS:
            end_composition(input); break;
        }
    }
    return CallWindowProcW(input->previous, hwnd, message, wparam, lparam);
}
#endif
int aqua_text_create(GLFWwindow* window, GLFWcharfun character) {
    TextInput* input = (TextInput*)calloc(1, sizeof(TextInput));
    if (!input) return 0;
    input->character = character;
    glfwSetWindowUserPointer(window, input);
#ifdef _WIN32
    input->hwnd = glfwGetWin32Window(window);
    if (!SetPropW(input->hwnd, property_name, window)) { glfwSetWindowUserPointer(window, NULL); free(input); return 0; }
    SetLastError(0);
    input->previous = (WNDPROC)SetWindowLongPtrW(input->hwnd, GWLP_WNDPROC, (LONG_PTR)text_proc);
    if (!input->previous) { RemovePropW(input->hwnd, property_name); glfwSetWindowUserPointer(window, NULL); free(input); return 0; }
#endif
    return 1;
}
void aqua_text_destroy(GLFWwindow* window) {
    TextInput* input = state(window);
    if (!input) return;
#ifdef _WIN32
    SetWindowLongPtrW(input->hwnd, GWLP_WNDPROC, (LONG_PTR)input->previous);
    RemovePropW(input->hwnd, property_name);
#endif
    clear_events(input); glfwSetWindowUserPointer(window, NULL); free(input);
}
AQUA void aqua_text_input(GLFWwindow* window, int enabled) {
    TextInput* input = state(window);
    if (input->enabled == enabled) return;
#ifdef _WIN32
    if (!enabled && input->composing) {
        HIMC context = ImmGetContext(input->hwnd);
        if (context) { ImmNotifyIME(context, NI_COMPOSITIONSTR, CPS_CANCEL, 0); ImmReleaseContext(input->hwnd, context); }
    }
#endif
    clear_events(input); input->composing = 0; input->enabled = enabled;
}
AQUA const char* aqua_text_event(GLFWwindow* window, int* kind, int* cursor) {
    TextInput* input = state(window);
    free(input->returned); input->returned = NULL;
    *kind = 0; *cursor = 0;
    if (input->failed) { clear_events(input); *kind = -1; return NULL; }
    TextEvent* event = input->first;
    if (!event) return NULL;
    input->first = event->next;
    if (!input->first) input->last = NULL;
    input->bytes -= event->size; input->returned = event;
    *kind = event->kind; *cursor = event->cursor;
    return event->text;
}
AQUA int aqua_text_composition_supported(void) {
#ifdef _WIN32
    return 1;
#else
    return 0;
#endif
}
AQUA void aqua_text_rect(GLFWwindow* window, int x, int y, int width, int height) {
#ifdef _WIN32
    TextInput* input = state(window);
    input->caret.left = x; input->caret.top = y;
    input->caret.right = x + width; input->caret.bottom = y + height;
    if (input->enabled) position_ime(input);
#else
    (void)window; (void)x; (void)y; (void)width; (void)height;
#endif
}
