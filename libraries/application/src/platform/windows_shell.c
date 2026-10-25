/* Windows services: the clipboard (Unicode text, PNG and DIBV5 images), IFileDialog pickers, GDI printing of a fitted
 * raster page, a read-only MSAA accessibility tree published through WM_GETOBJECT of the application's window, and
 * per-user Open With registration. Modal dialogs run on the runtime's thread, which owns the GLFW windows. */
#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#include "platform.h"
#include <windows.h>
#include <commctrl.h>
#include <commdlg.h>
#include <oleacc.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <stdlib.h>
#include <string.h>

static wchar_t *wide(const char *utf8) {
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, NULL, 0);
    wchar_t *w = malloc((size_t)(n > 0 ? n : 1) * sizeof *w);
    if (n <= 0 || !MultiByteToWideChar(CP_UTF8, 0, utf8, -1, w, n)) w[0] = 0;
    return w;
}
static char *narrow(const wchar_t *w, int length) {
    int n = WideCharToMultiByte(CP_UTF8, 0, w, length, NULL, 0, NULL, NULL);
    char *s = malloc((size_t)(n > 0 ? n : 0) + 1);
    if (n > 0) WideCharToMultiByte(CP_UTF8, 0, w, length, s, n, NULL, NULL);
    s[n > 0 ? n : 0] = 0;
    return s;
}

/* The window dialogs and the accessibility mirror attach to: this thread's active window, else its first application
   window (GLFW's window class; a visible one first), the runtime's windows living on the runtime's thread. */
typedef struct found { HWND visible, any; } found;
static BOOL CALLBACK application_windows(HWND hwnd, LPARAM data) {
    found *f = (found *)data;
    wchar_t name[32];
    if (!GetClassNameW(hwnd, name, 32) || wcscmp(name, L"GLFW30") != 0) return TRUE;
    if (!f->any) f->any = hwnd;
    if (IsWindowVisible(hwnd)) { f->visible = hwnd; return FALSE; }
    return TRUE;
}
static HWND application_window(void) {
    HWND hwnd = GetActiveWindow();
    if (hwnd) return hwnd;
    found f = {0};
    EnumThreadWindows(GetCurrentThreadId(), application_windows, (LPARAM)&f);
    return f.visible ? f.visible : f.any;
}

/* ---- Capabilities and launch documents ---- */

void app_os_capabilities(aqua_runtime *rt, aqua_value hash) {
    app_set(rt, hash, "files", app_capability(rt, true, NULL));
    app_set(rt, hash, "codecs", app_capability(rt, true, NULL));
    app_set(rt, hash, "persistentStorage", app_capability(rt, true, NULL));
    app_set(rt, hash, "pickers", app_capability(rt, true, NULL));
    app_set(rt, hash, "printing", app_capability(rt, true, NULL));
    app_set(rt, hash, "imageAcquisition", app_capability(rt, true, "Image acquisition selects an existing image; scanner/camera hardware adapter is not installed."));
    app_set(rt, hash, "fontEnumeration", app_capability(rt, true, NULL));
    app_set(rt, hash, "accessibility", app_capability(rt, true, "Read-only MSAA mirror; requires an active graphics window."));
    app_set(rt, hash, "fileAssociations", app_capability(rt, true, "Registration is explicit and per-user."));
    app_set(rt, hash, "activation", app_capability(rt, true, "Launch resources are supplied by the embedding host."));
}

/* ---- Pickers (IFileDialog) ---- */

static void com_ready(void) { CoInitializeEx(NULL, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE); }

static bool dialog_failure(app_error *e, HRESULT hr) { return app_error_set(e, APP_UNAVAILABLE, "File dialog error 0x%08lX", (unsigned long)hr); }

/* Shows a dialog configured for `options`; paths of the chosen items, none when the user cancelled. */
static bool run_dialog(const app_picker *options, int kind, char ***paths, size_t *count, app_error *e) {
    *paths = NULL;
    *count = 0;
    com_ready();
    IFileDialog *dialog = NULL;
    HRESULT hr = kind == 1 ? CoCreateInstance(&CLSID_FileSaveDialog, NULL, CLSCTX_INPROC_SERVER, &IID_IFileSaveDialog, (void **)&dialog)
                           : CoCreateInstance(&CLSID_FileOpenDialog, NULL, CLSCTX_INPROC_SERVER, &IID_IFileOpenDialog, (void **)&dialog);
    if (FAILED(hr)) return dialog_failure(e, hr);
    FILEOPENDIALOGOPTIONS flags = FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR | FOS_PATHMUSTEXIST;
    if (kind == 0) flags |= FOS_FILEMUSTEXIST | (options && options->multiple ? FOS_ALLOWMULTISELECT : 0);
    if (kind == 1) flags |= FOS_OVERWRITEPROMPT;
    if (kind == 2) flags |= FOS_PICKFOLDERS;
    IFileDialog_SetOptions(dialog, flags);
    if (options && options->title && options->title[0]) { wchar_t *w = wide(options->title); IFileDialog_SetTitle(dialog, w); free(w); }
    else if (kind == 2) IFileDialog_SetTitle(dialog, L"Select directory");
    if (options && options->suggested && options->suggested[0]) { wchar_t *w = wide(options->suggested); IFileDialog_SetFileName(dialog, w); free(w); }
    COMDLG_FILTERSPEC *specs = NULL;
    size_t spec_count = 0;
    if (kind != 2) {
        size_t filters = options && options->filter_count ? options->filter_count : 1;
        specs = calloc(filters, sizeof *specs);
        for (size_t i = 0; i < filters; i++) {
            if (!options || !options->filter_count) { specs[i].pszName = _wcsdup(L"All files"); specs[i].pszSpec = _wcsdup(L"*.*"); continue; }
            const app_filter *f = &options->filters[i];
            app_bytes pattern = {0};
            for (size_t k = 0; k < f->count; k++) {
                if (k) app_bytes_byte(&pattern, ';');
                app_bytes_text(&pattern, strcmp(f->extensions[k], "*") == 0 ? "*.*" : "*.");
                if (strcmp(f->extensions[k], "*") != 0) app_bytes_text(&pattern, f->extensions[k]);
            }
            app_bytes_byte(&pattern, 0);
            specs[i].pszName = wide(f->name);
            specs[i].pszSpec = wide((const char *)pattern.data);
            app_bytes_free(&pattern);
        }
        spec_count = filters;
        IFileDialog_SetFileTypes(dialog, (UINT)spec_count, specs);
        IFileDialog_SetFileTypeIndex(dialog, 1);
    }
    hr = IFileDialog_Show(dialog, application_window());
    bool ok = true;
    if (hr == HRESULT_FROM_WIN32(ERROR_CANCELLED)) ok = true;
    else if (FAILED(hr)) ok = dialog_failure(e, hr);
    else if (kind == 0) {
        IShellItemArray *items = NULL;
        if (SUCCEEDED(IFileOpenDialog_GetResults((IFileOpenDialog *)dialog, &items))) {
            DWORD n = 0;
            IShellItemArray_GetCount(items, &n);
            *paths = calloc(n ? n : 1, sizeof **paths);
            for (DWORD i = 0; i < n; i++) {
                IShellItem *item = NULL;
                wchar_t *name = NULL;
                if (SUCCEEDED(IShellItemArray_GetItemAt(items, i, &item)) && SUCCEEDED(IShellItem_GetDisplayName(item, SIGDN_FILESYSPATH, &name))) {
                    (*paths)[(*count)++] = narrow(name, -1);
                    CoTaskMemFree(name);
                }
                if (item) IShellItem_Release(item);
            }
            IShellItemArray_Release(items);
        }
    } else {
        IShellItem *item = NULL;
        wchar_t *name = NULL;
        if (SUCCEEDED(IFileDialog_GetResult(dialog, &item))) {
            if (SUCCEEDED(IShellItem_GetDisplayName(item, SIGDN_FILESYSPATH, &name))) {
                *paths = calloc(1, sizeof **paths);
                (*paths)[(*count)++] = narrow(name, -1);
                CoTaskMemFree(name);
            } else ok = app_error_set(e, APP_UNSUPPORTED, "Selection has no filesystem path.");
            IShellItem_Release(item);
        }
    }
    for (size_t i = 0; i < spec_count; i++) { free((void *)specs[i].pszName); free((void *)specs[i].pszSpec); }
    free(specs);
    IFileDialog_Release(dialog);
    return ok;
}

static void dialog(app_task *task, const app_picker *options, int kind, app_picked done) {
    app_error e = {0};
    char **paths;
    size_t count;
    if (app_task_cancelled(task)) return;
    if (!run_dialog(options, kind, &paths, &count, &e)) { done(task, &e, NULL, 0); return; }
    if (app_task_cancelled(task)) { for (size_t i = 0; i < count; i++) free(paths[i]); free(paths); return; }
    app_resolve_paths(task, (const char *const *)paths, count, done);
    for (size_t i = 0; i < count; i++) free(paths[i]);
    free(paths);
}

void app_os_open_files(app_task *task, const app_picker *options, app_picked done) { dialog(task, options, 0, done); }
void app_os_save_file(app_task *task, const app_picker *options, app_picked done) { dialog(task, options, 1, done); }
void app_os_pick_directory(app_task *task, app_picked done) { dialog(task, NULL, 2, done); }

/* ---- Clipboard ---- */

static UINT png_format(void) { static UINT format; if (!format) format = RegisterClipboardFormatW(L"PNG"); return format; }

static HWND open_clipboard(app_error *e) {
    HWND owner = CreateWindowExW(0, L"STATIC", L"Aquarius clipboard", 0, 0, 0, 0, 0, NULL, NULL, NULL, NULL);
    for (int attempt = 0; owner && attempt < 10; attempt++) {
        if (OpenClipboard(owner)) return owner;
        Sleep(10);
    }
    if (owner) DestroyWindow(owner);
    app_error_set(e, APP_UNAVAILABLE, "Clipboard is busy; retry after returning to the event loop.");
    return NULL;
}
static void close_clipboard(HWND owner) { CloseClipboard(); DestroyWindow(owner); }

static bool clipboard_bytes(UINT format, app_bytes *out, app_error *e) {
    memset(out, 0, sizeof *out);
    HANDLE handle = GetClipboardData(format);
    if (!handle) return true;
    SIZE_T size = GlobalSize(handle);
    if (!app_check_bytes(size, e)) return false;
    void *data = GlobalLock(handle);
    if (!data) return app_error_set(e, APP_UNAVAILABLE, "Clipboard data could not be locked.");
    app_bytes_append(out, data, size);
    GlobalUnlock(handle);
    if (!out->data) app_bytes_reserve(out, 1);
    return true;
}

void app_os_clipboard_capabilities(aqua_runtime *rt, aqua_value hash) {
    app_set(rt, hash, "text", app_capability(rt, true, NULL));
    app_set(rt, hash, "image", app_capability(rt, true, NULL));
    app_set(rt, hash, "multiple", app_capability(rt, true, NULL));
}

void app_os_clipboard_formats(app_task *task, void (*done)(app_task *, const app_error *, const char *const *, size_t)) {
    app_error e = {0};
    HWND owner = open_clipboard(&e);
    if (!owner) { done(task, &e, NULL, 0); return; }
    const char *formats[2];
    size_t count = 0;
    if (IsClipboardFormatAvailable(CF_UNICODETEXT)) formats[count++] = "text/plain";
    if (IsClipboardFormatAvailable(png_format()) || IsClipboardFormatAvailable(CF_DIBV5) || IsClipboardFormatAvailable(CF_DIB)) formats[count++] = "image/png";
    close_clipboard(owner);
    done(task, NULL, formats, count);
}

/* A DIB from the clipboard as a BMP file the codec reads. */
static bool dib_to_bmp(const app_bytes *dib, app_bytes *bmp, app_error *e) {
    memset(bmp, 0, sizeof *bmp);
    if (dib->length < 40) return app_error_set(e, APP_INVALID_DATA, "Clipboard DIB is truncated.");
    uint32_t header = app_get32le(dib->data), bits = app_get16le(dib->data + 14), compression = app_get32le(dib->data + 16), used = app_get32le(dib->data + 32);
    if (header < 40 || header > dib->length || used > 256) return app_error_set(e, APP_INVALID_DATA, "Unsupported clipboard DIB header.");
    uint32_t colors = bits <= 8 ? (used == 0 ? 1u << bits : used) : 0;
    uint32_t offset = 14 + header + colors * 4 + (header == 40 && compression == 3 ? 12 : 0);
    uint8_t file[14] = {'B', 'M'};
    app_put32le(file + 2, (uint32_t)(dib->length + 14));
    app_put32le(file + 10, offset);
    app_bytes_append(bmp, file, 14);
    app_bytes_append(bmp, dib->data, dib->length);
    return true;
}

void app_os_clipboard_read(app_task *task, bool image, void (*done)(app_task *, const app_error *, const char *, size_t, const uint8_t *, size_t)) {
    app_error e = {0};
    HWND owner = open_clipboard(&e);
    if (!owner) { done(task, &e, NULL, 0, NULL, 0); return; }
    app_bytes text = {0}, png = {0}, dib = {0};
    bool ok = clipboard_bytes(CF_UNICODETEXT, &text, &e);
    if (ok && image) {
        ok = clipboard_bytes(png_format(), &png, &e);
        if (ok && !png.data) { ok = clipboard_bytes(CF_DIBV5, &dib, &e); if (ok && !dib.data) ok = clipboard_bytes(CF_DIB, &dib, &e); }
    }
    close_clipboard(owner);
    char *utf8 = NULL;
    if (ok && text.data) {
        const wchar_t *units = (const wchar_t *)text.data;
        int n = (int)(text.length / 2);
        while (n > 0 && units[n - 1] == 0) n--;
        utf8 = narrow(units, n);
    }
    app_bytes bmp = {0};
    if (ok && dib.data) ok = dib_to_bmp(&dib, &bmp, &e);
    if (!ok) done(task, &e, NULL, 0, NULL, 0);
    else if (png.data) done(task, NULL, utf8, utf8 ? strlen(utf8) : 0, png.data, png.length);
    else done(task, NULL, utf8, utf8 ? strlen(utf8) : 0, bmp.data, bmp.length);
    free(utf8);
    app_bytes_free(&text); app_bytes_free(&png); app_bytes_free(&dib); app_bytes_free(&bmp);
}

static bool put_clipboard(UINT format, const void *data, size_t length, app_error *e) {
    HGLOBAL handle = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, length);
    if (!handle) return app_error_set(e, APP_UNAVAILABLE, "Clipboard allocation failed.");
    void *target = GlobalLock(handle);
    if (!target) { GlobalFree(handle); return app_error_set(e, APP_UNAVAILABLE, "Clipboard allocation cannot be locked."); }
    memcpy(target, data, length);
    GlobalUnlock(handle);
    if (!SetClipboardData(format, handle)) { GlobalFree(handle); return app_error_set(e, APP_UNAVAILABLE, "Publishing clipboard data failed."); }
    return true;
}

void app_os_clipboard_write(app_task *task, const char *text, size_t text_length, const app_picture *image, app_done done) {
    app_error e = {0};
    if (!text && !image) { app_error_set(&e, APP_INVALID_DATA, "Clipboard content is empty."); done(task, &e); return; }
    wchar_t *units = NULL;
    int unit_count = 0;
    if (text) {
        unit_count = MultiByteToWideChar(CP_UTF8, 0, text, (int)text_length, NULL, 0);
        units = calloc((size_t)unit_count + 1, sizeof *units);
        if (unit_count) MultiByteToWideChar(CP_UTF8, 0, text, (int)text_length, units, unit_count);
    }
    app_bytes png = {0}, dib = {0};
    bool ok = true;
    if (image) {
        app_encode_options options;
        app_encode_defaults(&options);
        ok = app_image_encode(image, APP_PNG, &options, &png, &e);
        if (ok) {
            size_t pixels = (size_t)image->width * image->height * 4;
            app_bytes_reserve(&dib, 124 + pixels);
            memset(dib.data, 0, 124);
            app_put32le(dib.data, 124);
            app_put32le(dib.data + 4, (uint32_t)image->width);
            app_put32le(dib.data + 8, (uint32_t)-image->height);
            app_put16le(dib.data + 12, 1);
            app_put16le(dib.data + 14, 32);
            app_put32le(dib.data + 16, 3);
            app_put32le(dib.data + 40, 0x00ff0000u);
            app_put32le(dib.data + 44, 0x0000ff00u);
            app_put32le(dib.data + 48, 0x000000ffu);
            app_put32le(dib.data + 52, 0xff000000u);
            app_put32le(dib.data + 56, 0x73524742u);
            for (size_t i = 0; i < pixels; i += 4) {
                dib.data[124 + i] = image->rgba[i + 2];
                dib.data[125 + i] = image->rgba[i + 1];
                dib.data[126 + i] = image->rgba[i];
                dib.data[127 + i] = image->rgba[i + 3];
            }
            dib.length = 124 + pixels;
        }
    }
    if (ok && app_task_cancelled(task)) { free(units); app_bytes_free(&png); app_bytes_free(&dib); return; }
    HWND owner = ok ? open_clipboard(&e) : NULL;
    if (owner) {
        if (!EmptyClipboard()) ok = app_error_set(&e, APP_UNAVAILABLE, "Clipboard cannot be cleared.");
        if (ok && units) ok = put_clipboard(CF_UNICODETEXT, units, ((size_t)unit_count + 1) * sizeof *units, &e);
        if (ok && png.data) ok = put_clipboard(png_format(), png.data, png.length, &e);
        if (ok && dib.data) ok = put_clipboard(CF_DIBV5, dib.data, dib.length, &e);
        close_clipboard(owner);
    } else ok = false;
    free(units);
    app_bytes_free(&png);
    app_bytes_free(&dib);
    done(task, ok ? NULL : &e);
}

/* ---- Printing ---- */

void app_os_print(app_task *task, const app_picture *picture, app_done done) {
    app_error e = {0};
    if (app_task_cancelled(task)) return;
    PRINTDLGW request = {0};
    request.lStructSize = sizeof request;
    request.hwndOwner = application_window();
    request.Flags = PD_RETURNDC | PD_USEDEVMODECOPIESANDCOLLATE;
    request.nCopies = 1;
    if (!PrintDlgW(&request)) {
        DWORD code = CommDlgExtendedError();
        if (code) app_error_set(&e, APP_UNAVAILABLE, "Print dialog error %lu", (unsigned long)code);
        else app_error_set(&e, APP_CANCELLED, "Print dialog cancelled.");
        done(task, &e);
        return;
    }
    HDC dc = request.hDC;
    bool started = false, ok = true;
    DOCINFOW info = {sizeof info, L"Aquarius document", NULL, NULL, 0};
    if (task->token && task->token->cancelled) ok = app_error_set(&e, APP_CANCELLED, "The operation was canceled.");
    else if (StartDocW(dc, &info) <= 0) ok = app_error_set(&e, APP_UNAVAILABLE, "Printer cannot start document.");
    else started = true;
    if (ok && StartPage(dc) <= 0) ok = app_error_set(&e, APP_UNAVAILABLE, "Printer cannot start page.");
    if (ok) {
        /* The page is opaque: transparency is flattened onto white. */
        size_t count = (size_t)picture->width * picture->height;
        uint8_t *bgra = malloc(count * 4);
        for (size_t i = 0; i < count; i++) {
            uint32_t a = picture->rgba[i * 4 + 3];
            for (int c = 0; c < 3; c++) bgra[i * 4 + 2 - c] = (uint8_t)((picture->rgba[i * 4 + c] * a + 255 * (255 - a) + 127) / 255);
            bgra[i * 4 + 3] = 0;
        }
        BITMAPINFO bitmap = {0};
        bitmap.bmiHeader.biSize = sizeof bitmap.bmiHeader;
        bitmap.bmiHeader.biWidth = picture->width;
        bitmap.bmiHeader.biHeight = -picture->height;
        bitmap.bmiHeader.biPlanes = 1;
        bitmap.bmiHeader.biBitCount = 32;
        double sx = (double)GetDeviceCaps(dc, HORZRES) / picture->width, sy = (double)GetDeviceCaps(dc, VERTRES) / picture->height;
        double scale = sx < sy ? sx : sy;
        int copied = StretchDIBits(dc, 0, 0, (int)(picture->width * scale), (int)(picture->height * scale), 0, 0, picture->width,
                                   picture->height, bgra, &bitmap, DIB_RGB_COLORS, SRCCOPY);
        free(bgra);
        if (copied == 0 || copied == GDI_ERROR) ok = app_error_set(&e, APP_UNAVAILABLE, "Printer cannot render image.");
    }
    if (ok && (EndPage(dc) <= 0 || EndDoc(dc) <= 0)) ok = app_error_set(&e, APP_UNAVAILABLE, "Printer could not finish document.");
    else if (ok) started = false;
    if (started) AbortDoc(dc);
    DeleteDC(dc);
    if (request.hDevMode) GlobalFree(request.hDevMode);
    if (request.hDevNames) GlobalFree(request.hDevNames);
    done(task, ok ? NULL : &e);
}

/* ---- Accessibility: a read-only MSAA tree (client object with one simple child per node) ---- */

typedef struct mirror {
    IAccessible accessible;     /* first: the COM object */
    LONG references;
    HWND hwnd;
    app_node *nodes; size_t count;
} mirror;

static mirror *mirror_of(IAccessible *a) { return (mirror *)a; }
static void nodes_free(mirror *m) {
    for (size_t i = 0; i < m->count; i++) { free(m->nodes[i].id); free(m->nodes[i].role); free(m->nodes[i].label); free(m->nodes[i].value); }
    free(m->nodes);
    m->nodes = NULL;
    m->count = 0;
}

static HRESULT STDMETHODCALLTYPE query_interface(IAccessible *a, REFIID id, void **out) {
    if (IsEqualIID(id, &IID_IUnknown) || IsEqualIID(id, &IID_IDispatch) || IsEqualIID(id, &IID_IAccessible)) {
        *out = a;
        IAccessible_AddRef(a);
        return S_OK;
    }
    *out = NULL;
    return E_NOINTERFACE;
}
static ULONG STDMETHODCALLTYPE add_ref(IAccessible *a) { return (ULONG)InterlockedIncrement(&mirror_of(a)->references); }
static ULONG STDMETHODCALLTYPE release(IAccessible *a) {
    mirror *m = mirror_of(a);
    LONG n = InterlockedDecrement(&m->references);
    if (n == 0) { nodes_free(m); free(m); }
    return (ULONG)n;
}
static HRESULT STDMETHODCALLTYPE type_info_count(IAccessible *a, UINT *n) { (void)a; *n = 0; return S_OK; }
static HRESULT STDMETHODCALLTYPE type_info(IAccessible *a, UINT i, LCID l, ITypeInfo **t) { (void)a; (void)i; (void)l; *t = NULL; return E_NOTIMPL; }
static HRESULT STDMETHODCALLTYPE ids_of_names(IAccessible *a, REFIID r, LPOLESTR *n, UINT c, LCID l, DISPID *d) { (void)a; (void)r; (void)n; (void)c; (void)l; (void)d; return E_NOTIMPL; }
static HRESULT STDMETHODCALLTYPE invoke(IAccessible *a, DISPID d, REFIID r, LCID l, WORD f, DISPPARAMS *p, VARIANT *v, EXCEPINFO *x, UINT *u) {
    (void)a; (void)d; (void)r; (void)l; (void)f; (void)p; (void)v; (void)x; (void)u;
    return E_NOTIMPL;
}

/* The node of a child id 1..count, or NULL for the client object (CHILDID_SELF); *bad for any other id. */
static const app_node *node_of(mirror *m, VARIANT child, bool *bad) {
    *bad = false;
    if (child.vt != VT_I4) { *bad = true; return NULL; }
    if (child.lVal == CHILDID_SELF) return NULL;
    if (child.lVal < 1 || (size_t)child.lVal > m->count) { *bad = true; return NULL; }
    return &m->nodes[child.lVal - 1];
}

static BSTR bstr(const char *utf8) {
    if (!utf8) return NULL;
    wchar_t *w = wide(utf8);
    BSTR b = SysAllocString(w);
    free(w);
    return b;
}

static HRESULT STDMETHODCALLTYPE get_parent(IAccessible *a, IDispatch **parent) { (void)a; *parent = NULL; return S_FALSE; }
static HRESULT STDMETHODCALLTYPE get_child_count(IAccessible *a, long *count) { *count = (long)mirror_of(a)->count; return S_OK; }
static HRESULT STDMETHODCALLTYPE get_child(IAccessible *a, VARIANT child, IDispatch **out) {
    bool bad;
    *out = NULL;
    node_of(mirror_of(a), child, &bad);
    return bad ? E_INVALIDARG : S_FALSE;
}
static HRESULT STDMETHODCALLTYPE get_name(IAccessible *a, VARIANT child, BSTR *name) {
    bool bad;
    const app_node *n = node_of(mirror_of(a), child, &bad);
    if (bad) return E_INVALIDARG;
    *name = bstr(n ? n->label : "Aquarius application");
    return S_OK;
}
static HRESULT STDMETHODCALLTYPE get_value(IAccessible *a, VARIANT child, BSTR *value) {
    bool bad;
    const app_node *n = node_of(mirror_of(a), child, &bad);
    if (bad) return E_INVALIDARG;
    *value = n ? bstr(n->value) : NULL;
    return *value ? S_OK : S_FALSE;
}
static HRESULT STDMETHODCALLTYPE get_none(IAccessible *a, VARIANT child, BSTR *text) {
    bool bad;
    node_of(mirror_of(a), child, &bad);
    *text = NULL;
    return bad ? E_INVALIDARG : S_FALSE;
}
static HRESULT STDMETHODCALLTYPE get_role(IAccessible *a, VARIANT child, VARIANT *role) {
    bool bad;
    const app_node *n = node_of(mirror_of(a), child, &bad);
    if (bad) return E_INVALIDARG;
    long value = ROLE_SYSTEM_CLIENT;
    if (n) {
        static const struct { const char *name; long role; } roles[] = {
            {"button", ROLE_SYSTEM_PUSHBUTTON}, {"textbox", ROLE_SYSTEM_TEXT}, {"img", ROLE_SYSTEM_GRAPHIC},
            {"status", ROLE_SYSTEM_STATUSBAR}, {"checkbox", ROLE_SYSTEM_CHECKBUTTON}, {"list", ROLE_SYSTEM_LIST},
            {"listitem", ROLE_SYSTEM_LISTITEM}};
        value = ROLE_SYSTEM_STATICTEXT;
        for (size_t i = 0; i < sizeof roles / sizeof *roles; i++) if (strcmp(n->role, roles[i].name) == 0) value = roles[i].role;
    }
    role->vt = VT_I4;
    role->lVal = value;
    return S_OK;
}
static HRESULT STDMETHODCALLTYPE get_state(IAccessible *a, VARIANT child, VARIANT *state) {
    bool bad;
    const app_node *n = node_of(mirror_of(a), child, &bad);
    if (bad) return E_INVALIDARG;
    state->vt = VT_I4;
    state->lVal = n && n->disabled ? STATE_SYSTEM_UNAVAILABLE : 0;
    return S_OK;
}
static HRESULT STDMETHODCALLTYPE get_help_topic(IAccessible *a, BSTR *file, VARIANT child, long *topic) { (void)a; (void)child; *file = NULL; *topic = -1; return S_FALSE; }
static HRESULT STDMETHODCALLTYPE get_focus(IAccessible *a, VARIANT *focus) { (void)a; focus->vt = VT_I4; focus->lVal = CHILDID_SELF; return S_OK; }
static HRESULT STDMETHODCALLTYPE get_selection(IAccessible *a, VARIANT *selection) { (void)a; selection->vt = VT_EMPTY; return S_FALSE; }
static HRESULT STDMETHODCALLTYPE select_child(IAccessible *a, long flags, VARIANT child) { (void)a; (void)flags; (void)child; return E_NOTIMPL; }
static HRESULT STDMETHODCALLTYPE location(IAccessible *a, long *x, long *y, long *w, long *h, VARIANT child) {
    (void)child;
    RECT r = {0};
    GetWindowRect(mirror_of(a)->hwnd, &r);
    *x = r.left; *y = r.top; *w = r.right - r.left; *h = r.bottom - r.top;
    return S_OK;
}
static HRESULT STDMETHODCALLTYPE navigate(IAccessible *a, long direction, VARIANT start, VARIANT *end) {
    mirror *m = mirror_of(a);
    long n = start.vt == VT_I4 ? start.lVal : 0, count = (long)m->count, next = 0;
    if (direction == NAVDIR_FIRSTCHILD && count > 0) next = 1;
    else if (direction == NAVDIR_LASTCHILD && count > 0) next = count;
    else if (direction == NAVDIR_NEXT && n >= 1 && n < count) next = n + 1;
    else if (direction == NAVDIR_PREVIOUS && n > 1) next = n - 1;
    if (!next) { end->vt = VT_EMPTY; return S_FALSE; }
    end->vt = VT_I4;
    end->lVal = next;
    return S_OK;
}
static HRESULT STDMETHODCALLTYPE hit_test(IAccessible *a, long x, long y, VARIANT *child) { (void)a; (void)x; (void)y; child->vt = VT_I4; child->lVal = CHILDID_SELF; return S_OK; }
static HRESULT STDMETHODCALLTYPE default_action(IAccessible *a, VARIANT child) { (void)a; (void)child; return E_NOTIMPL; }
static HRESULT STDMETHODCALLTYPE put_text(IAccessible *a, VARIANT child, BSTR text) { (void)a; (void)child; (void)text; return E_NOTIMPL; }

static IAccessibleVtbl accessible_table = {
    .QueryInterface = query_interface, .AddRef = add_ref, .Release = release, .GetTypeInfoCount = type_info_count,
    .GetTypeInfo = type_info, .GetIDsOfNames = ids_of_names, .Invoke = invoke, .get_accParent = get_parent,
    .get_accChildCount = get_child_count, .get_accChild = get_child, .get_accName = get_name, .get_accValue = get_value,
    .get_accDescription = get_none, .get_accRole = get_role, .get_accState = get_state, .get_accHelp = get_none,
    .get_accHelpTopic = get_help_topic, .get_accKeyboardShortcut = get_none, .get_accFocus = get_focus,
    .get_accSelection = get_selection, .get_accDefaultAction = get_none, .accSelect = select_child,
    .accLocation = location, .accNavigate = navigate, .accHitTest = hit_test, .accDoDefaultAction = default_action,
    .put_accName = put_text, .put_accValue = put_text,
};

enum { SUBCLASS_ID = 0x41515541 };

static LRESULT CALLBACK accessibility_proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR id, DWORD_PTR data) {
    app_state *state = (app_state *)data;
    mirror *m = state->platform;
    if (message == WM_GETOBJECT && (LONG)lparam == OBJID_CLIENT && m)
        return LresultFromObject(&IID_IAccessible, wparam, (IUnknown *)&m->accessible);
    if (message == WM_NCDESTROY) {
        RemoveWindowSubclass(hwnd, accessibility_proc, id);
        if (m && m->hwnd == hwnd) { state->platform = NULL; IAccessible_Release(&m->accessible); }
    }
    return DefSubclassProc(hwnd, message, wparam, lparam);
}

bool app_os_accessibility(app_state *state, const app_node *nodes, size_t count, app_error *e) {
    HWND hwnd = application_window();
    mirror *m = state->platform;
    if (m && m->hwnd != hwnd && hwnd) { RemoveWindowSubclass(m->hwnd, accessibility_proc, SUBCLASS_ID); state->platform = NULL; IAccessible_Release(&m->accessible); m = NULL; }
    if (!m) {
        if (!hwnd) return app_error_set(e, APP_UNAVAILABLE, "No active graphics window accessibility adapter.");
        m = calloc(1, sizeof *m);
        m->accessible.lpVtbl = &accessible_table;
        m->references = 1;
        m->hwnd = hwnd;
        if (!SetWindowSubclass(hwnd, accessibility_proc, SUBCLASS_ID, (DWORD_PTR)state)) {
            free(m);
            return app_error_set(e, APP_UNAVAILABLE, "The accessibility mirror cannot attach to the window.");
        }
        state->platform = m;
    }
    nodes_free(m);
    m->nodes = calloc(count ? count : 1, sizeof *m->nodes);
    for (size_t i = 0; i < count; i++) {
        m->nodes[i].id = app_strdup(nodes[i].id);
        m->nodes[i].role = app_strdup(nodes[i].role);
        m->nodes[i].label = app_strdup(nodes[i].label);
        m->nodes[i].value = app_strdup(nodes[i].value);
        m->nodes[i].disabled = nodes[i].disabled;
    }
    m->count = count;
    NotifyWinEvent(EVENT_OBJECT_REORDER, hwnd, OBJID_CLIENT, CHILDID_SELF);
    return true;
}

void app_platform_close(app_state *state) {
    mirror *m = state->platform;
    if (!m) return;
    RemoveWindowSubclass(m->hwnd, accessibility_proc, SUBCLASS_ID);
    state->platform = NULL;
    IAccessible_Release(&m->accessible);
}

/* ---- File associations: an Open With handler for the current user, never the default-app choice ---- */

static void quote(app_bytes *out, const char *value) {
    app_bytes_byte(out, '"');
    size_t slashes = 0;
    for (const char *c = value; *c; c++) {
        if (*c == '\\') { slashes++; continue; }
        for (size_t i = 0; i < (*c == '"' ? slashes * 2 + 1 : slashes); i++) app_bytes_byte(out, '\\');
        app_bytes_byte(out, (uint8_t)*c);
        slashes = 0;
    }
    for (size_t i = 0; i < slashes * 2; i++) app_bytes_byte(out, '\\');
    app_bytes_byte(out, '"');
}

static bool set_default(HKEY root, const wchar_t *path, const wchar_t *value, app_error *e) {
    HKEY key;
    if (RegCreateKeyExW(root, path, 0, NULL, 0, KEY_SET_VALUE, NULL, &key, NULL) != ERROR_SUCCESS)
        return app_error_set(e, APP_PERMISSION_DENIED, "The registry key cannot be created.");
    LSTATUS status = RegSetValueExW(key, NULL, 0, REG_SZ, (const BYTE *)value, (DWORD)((wcslen(value) + 1) * sizeof *value));
    RegCloseKey(key);
    return status == ERROR_SUCCESS || app_error_set(e, APP_PERMISSION_DENIED, "The registry value cannot be written.");
}

bool app_os_register_association(const app_association *a, app_error *e) {
    const char *extension = a->extension[0] == '.' ? a->extension + 1 : a->extension;
    size_t n = strlen(extension);
    bool valid = n >= 1 && n <= 32;
    for (size_t i = 0; i < n && valid; i++) valid = (extension[i] >= 'a' && extension[i] <= 'z') || (extension[i] >= 'A' && extension[i] <= 'Z') || (extension[i] >= '0' && extension[i] <= '9');
    if (!valid) return app_error_set(e, APP_INVALID_DATA, "Invalid file association extension.");
    size_t id_length = strlen(a->application);
    valid = id_length >= 1 && id_length <= 128 && strcmp(a->application, ".") != 0 && strcmp(a->application, "..") != 0;
    for (size_t i = 0; i < id_length && valid; i++) {
        char c = a->application[i];
        valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
    }
    if (!valid) return app_error_set(e, APP_INVALID_DATA, "Storage key must contain 1..128 ASCII letters, digits, dots, hyphens or underscores.");
    char *executable = app_os_full_path(a->executable, e);
    if (!executable) return false;
    app_stat s;
    app_os_stat(executable, &s, e);
    if (!s.exists || s.kind != APP_FILE) { app_error_set(e, APP_NOT_FOUND, "Association executable does not exist."); free(executable); return false; }
    app_bytes command = {0};
    quote(&command, executable);
    free(executable);
    for (size_t i = 0; i < a->count; i++) { app_bytes_byte(&command, ' '); quote(&command, a->arguments[i]); }
    app_bytes_text(&command, " \"%1\"");
    app_bytes_byte(&command, 0);
    char *program = app_printf("Aquarius.%s.%s", a->application, extension);
    char *class_path = app_printf("Software\\Classes\\%s", program), *open_path = app_printf("Software\\Classes\\%s\\shell\\open\\command", program);
    char *extension_path = app_printf("Software\\Classes\\.%s\\OpenWithProgids", extension);
    wchar_t *wclass = wide(class_path), *wopen = wide(open_path), *wext = wide(extension_path), *wprogram = wide(program);
    wchar_t *wdescription = wide(a->description), *wcommand = wide((const char *)command.data);
    bool ok = set_default(HKEY_CURRENT_USER, wclass, wdescription, e) && set_default(HKEY_CURRENT_USER, wopen, wcommand, e);
    if (ok) {
        HKEY key;
        ok = RegCreateKeyExW(HKEY_CURRENT_USER, wext, 0, NULL, 0, KEY_SET_VALUE, NULL, &key, NULL) == ERROR_SUCCESS;
        if (ok) { ok = RegSetValueExW(key, wprogram, 0, REG_NONE, NULL, 0) == ERROR_SUCCESS; RegCloseKey(key); }
        if (!ok) app_error_set(e, APP_PERMISSION_DENIED, "The registry value cannot be written.");
    }
    if (ok) SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, NULL, NULL);
    free(program); free(class_path); free(open_path); free(extension_path);
    free(wclass); free(wopen); free(wext); free(wprogram); free(wdescription); free(wcommand);
    app_bytes_free(&command);
    return ok;
}
