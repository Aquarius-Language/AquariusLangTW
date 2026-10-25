// Windows on the web: one <canvas> per aqua/window.h window inside #surfaces, with DOM input translated to the same
// events and GLFW key codes the desktop window system produces (runtime/platform/web/window.c). Events queue here and
// are dispatched to C during window_poll, in arrival order.
import { registerGlue } from '../core/glue.mjs';

const NAMED = {
    Space: 32, Quote: 39, Comma: 44, Minus: 45, Period: 46, Slash: 47, Semicolon: 59, Equal: 61, BracketLeft: 91, Backslash: 92,
    BracketRight: 93, Backquote: 96, Escape: 256, Enter: 257, Tab: 258, Backspace: 259, Insert: 260, Delete: 261, ArrowRight: 262,
    ArrowLeft: 263, ArrowDown: 264, ArrowUp: 265, PageUp: 266, PageDown: 267, Home: 268, End: 269, CapsLock: 280, ScrollLock: 281,
    NumLock: 282, PrintScreen: 283, Pause: 284, NumpadDecimal: 330, NumpadDivide: 331, NumpadMultiply: 332, NumpadSubtract: 333,
    NumpadAdd: 334, NumpadEnter: 335, NumpadEqual: 336, ShiftLeft: 340, ControlLeft: 341, AltLeft: 342, MetaLeft: 343,
    ShiftRight: 344, ControlRight: 345, AltRight: 346, MetaRight: 347, ContextMenu: 348,
};
/** GLFW key code of a KeyboardEvent.code, or -1. */
export function glfwKey(code) {
    if (code in NAMED) return NAMED[code];
    let m = /^Key([A-Z])$/.exec(code); if (m) return m[1].charCodeAt(0);
    m = /^Digit([0-9])$/.exec(code); if (m) return 48 + Number(m[1]);
    m = /^Numpad([0-9])$/.exec(code); if (m) return 320 + Number(m[1]);
    m = /^F([1-9]|1[0-9]|2[0-5])$/.exec(code); if (m) return 289 + Number(m[1]);
    return -1;
}
const modifiers = (e) => (e.shiftKey ? 1 : 0) | (e.ctrlKey ? 2 : 0) | (e.altKey ? 4 : 0) | (e.metaKey ? 8 : 0) |
    (e.getModifierState?.('CapsLock') ? 16 : 0) | (e.getModifierState?.('NumLock') ? 32 : 0);
// DOM buttons 0 left, 1 middle, 2 right; GLFW 0 left, 1 right, 2 middle.
const glfwButton = (b) => (b === 1 ? 2 : b === 2 ? 1 : b);
const CURSORS = { 0x36001: 'default', 0x36002: 'text', 0x36003: 'crosshair', 0x36004: 'pointer', 0x36005: 'ew-resize', 0x36006: 'ns-resize',
    0x36007: 'nwse-resize', 0x36008: 'nesw-resize', 0x36009: 'move', 0x3600a: 'not-allowed' };
const EVENT = { KEY: 1, POINTER: 2, BUTTON: 3, SCROLL: 4, FOCUS: 5, VISIBLE: 6, ENTER: 7, DROP: 8, CLOSE: 9, CAPTURE_LOST: 10, TEXT: 12,
    COMPOSITION_START: 13, COMPOSITION_UPDATE: 14, COMPOSITION_END: 15, RESIZE: 16 };

registerGlue('window', (context) => {
    let clipboard = '';
    document.addEventListener?.('paste', (e) => { clipboard = e.clipboardData?.getData('text') ?? clipboard; });
    const windowOf = (handle) => context.object(handle);
    const container = () => document.getElementById('surfaces') ?? document.body;

    class PageWindow {
        constructor(pointer, width, height, title, flags) {
            this.pointer = pointer;   // the C aqua_window
            this.queue = [];
            this.keys = new Set(); this.buttons = new Set(); this.x = 0; this.y = 0;
            this.canvas = document.createElement('canvas');
            this.canvas.tabIndex = 0;
            this.canvas.style.touchAction = 'none';
            this.canvas.setAttribute('aria-label', title);
            this.fullscreen = (flags & 8) !== 0;
            this.width = width; this.height = height;
            this.applySize();
            if (!(flags & 2)) container().append(this.canvas);
            if (title) document.title = title;
            this.abort = new AbortController();
            this.listen();
            this.canvas.focus();
        }
        applySize() {
            const style = this.canvas.style;
            if (this.fullscreen) { style.width = '100vw'; style.height = '100vh'; }
            else { style.width = this.width + 'px'; style.height = this.height + 'px'; }
            const box = this.canvas.isConnected ? this.canvas.getBoundingClientRect() : { width: this.width, height: this.height };
            const density = globalThis.devicePixelRatio || 1;
            this.logical = [Math.max(1, Math.round(box.width || this.width)), Math.max(1, Math.round(box.height || this.height))];
            const pixels = [Math.max(1, Math.round(this.logical[0] * density)), Math.max(1, Math.round(this.logical[1] * density))];
            if (this.canvas.width !== pixels[0]) this.canvas.width = pixels[0];
            if (this.canvas.height !== pixels[1]) this.canvas.height = pixels[1];
        }
        push(type, fields = {}) { this.queue.push({ type, ...fields }); context.wake(); }
        position(e) { const r = this.canvas.getBoundingClientRect(); this.x = e.clientX - r.left; this.y = e.clientY - r.top; }
        listen() {
            const signal = this.abort.signal, c = this.canvas;
            const key = (e, action) => {
                if (this.composing || e.isComposing) return;
                const code = glfwKey(e.code);
                if (action) this.keys.add(code); else this.keys.delete(code);
                this.push(EVENT.KEY, { key: code, action: action && e.repeat ? 2 : action, mods: modifiers(e), text: e.key?.length === 1 ? e.key : null });
                if (action && !this.textEnabled && e.key?.length === 1 && !e.ctrlKey && !e.metaKey) this.push(EVENT.TEXT, { text: e.key });
                // Keep game keys from scrolling or moving focus; leave shortcuts with modifiers to the browser.
                if (!e.ctrlKey && !e.metaKey && !e.altKey && (code < 290 || code > 314) && document.activeElement === c) e.preventDefault();
            };
            window.addEventListener('keydown', (e) => key(e, 1), { signal });
            window.addEventListener('keyup', (e) => key(e, 0), { signal });
            const pointer = (e) => ({ x: this.x, y: this.y, pointer: e.pointerType === 'mouse' ? 0 : e.pointerId, pointerType: e.pointerType === 'pen' ? 2 : e.pointerType === 'touch' ? 1 : 0, pressure: e.pressure ?? 0.5 });
            c.addEventListener('pointerdown', (e) => {
                this.position(e); c.setPointerCapture?.(e.pointerId); c.focus();
                const b = glfwButton(e.button); this.buttons.add(b);
                this.push(EVENT.POINTER, pointer(e));
                this.push(EVENT.BUTTON, { ...pointer(e), button: b, action: 1, mods: modifiers(e) });
                e.preventDefault();
            }, { signal });
            const release = (e) => {
                this.position(e);
                const b = glfwButton(e.button);
                if (!this.buttons.delete(b)) return;
                this.push(EVENT.BUTTON, { ...pointer(e), button: b, action: 0, mods: modifiers(e) });
            };
            c.addEventListener('pointerup', release, { signal });
            c.addEventListener('pointercancel', release, { signal });
            c.addEventListener('pointermove', (e) => {
                for (const sample of e.getCoalescedEvents?.() ?? [e]) { this.position(sample); this.push(EVENT.POINTER, { ...pointer(sample), dx: sample.movementX, dy: sample.movementY }); }
            }, { signal });
            c.addEventListener('pointerenter', () => this.push(EVENT.ENTER, { action: 1 }), { signal });
            c.addEventListener('pointerleave', () => this.push(EVENT.ENTER, { action: 0 }), { signal });
            c.addEventListener('wheel', (e) => {
                const unit = e.deltaMode === 0 ? 100 : e.deltaMode === 1 ? 3 : 1;
                this.push(EVENT.SCROLL, { x: -e.deltaX / unit, y: -e.deltaY / unit });
                e.preventDefault();
            }, { signal, passive: false });
            c.addEventListener('contextmenu', (e) => e.preventDefault(), { signal });
            c.addEventListener('lostpointercapture', () => this.push(EVENT.CAPTURE_LOST), { signal });
            c.addEventListener('dragover', (e) => e.preventDefault(), { signal });
            c.addEventListener('drop', (e) => {
                e.preventDefault();
                const files = [...(e.dataTransfer?.files ?? [])];
                files.forEach((file, index) => this.push(EVENT.DROP, { text: file.name, index, count: files.length }));
                context.droppedFiles = files;
            }, { signal });
            const blur = () => {
                for (const k of this.keys) this.push(EVENT.KEY, { key: k, action: 0 });
                for (const b of this.buttons) this.push(EVENT.BUTTON, { button: b, action: 0, x: this.x, y: this.y });
                this.keys.clear(); this.buttons.clear();
                this.push(EVENT.FOCUS, { action: 0 });
            };
            window.addEventListener('blur', blur, { signal });
            window.addEventListener('focus', () => this.push(EVENT.FOCUS, { action: 1 }), { signal });
            document.addEventListener('visibilitychange', () => this.push(EVENT.VISIBLE, { action: document.hidden ? 0 : 1 }), { signal });
            window.addEventListener('resize', () => this.resized(), { signal });
            // Text input: a hidden textarea receives typed text and input-method composition.
            const text = this.textarea = document.createElement('textarea');
            Object.assign(text.style, { position: 'fixed', opacity: '0.01', width: '1px', height: '1px', pointerEvents: 'none', left: '0', top: '0' });
            text.setAttribute('aria-label', 'Aquarius text input');
            document.body.append(text);
            text.addEventListener('input', (e) => { if (!e.isComposing && e.data) { this.push(EVENT.TEXT, { text: e.data }); text.value = ''; } }, { signal });
            text.addEventListener('compositionstart', () => { this.composing = true; this.push(EVENT.COMPOSITION_START, { text: '' }); }, { signal });
            text.addEventListener('compositionupdate', (e) => this.push(EVENT.COMPOSITION_UPDATE, { text: e.data, cursor: e.data.length }), { signal });
            text.addEventListener('compositionend', (e) => {
                this.composing = false;
                this.push(EVENT.COMPOSITION_END, { text: '' });
                if (e.data) this.push(EVENT.TEXT, { text: e.data });
                text.value = '';
            }, { signal });
        }
        resized() {
            const before = this.logical;
            this.applySize();
            if (before[0] !== this.logical[0] || before[1] !== this.logical[1]) this.push(EVENT.RESIZE, { x: this.logical[0], y: this.logical[1] });
        }
        dispatch() {
            const events = this.queue;
            this.queue = [];
            for (const e of events) {
                const [text] = e.text !== undefined && e.text !== null ? context.allocate(e.text) : [0];
                context.call('aqua_web_window_event', this.pointer, e.type, e.key ?? 0, 0, e.action ?? 0, e.mods ?? 0, e.button ?? 0,
                    e.x ?? 0, e.y ?? 0, e.pointer ?? 0, e.pointerType ?? 0, e.pressure ?? 0, text, e.cursor ?? 0);
                if (text) context.call('free', text);
            }
        }
        dispose() { this.abort.abort(); this.canvas.remove(); this.textarea.remove(); if (document.pointerLockElement === this.canvas) document.exitPointerLock(); }
    }

    return {
        window_create(pointer, width, height, title, flags) {
            const window = new PageWindow(pointer, width, height, context.text(title), flags);
            const handle = context.retain(window);
            window.canvas.id = `aqua-window-${handle}`; // WebGPU surfaces select the canvas by this id
            return handle;
        },
        window_destroy(handle) { windowOf(handle)?.dispose(); context.release(handle); },
        window_poll(handle) { const w = windowOf(handle); w.resized(); w.dispatch(); },
        window_size(handle, out) {
            const w = windowOf(handle), v = context.view;
            v.setInt32(out, w.logical[0], true); v.setInt32(out + 4, w.logical[1], true);
            v.setInt32(out + 8, w.canvas.width, true); v.setInt32(out + 12, w.canvas.height, true);
        },
        window_scale: () => globalThis.devicePixelRatio || 1,
        window_set_size(handle, width, height) { const w = windowOf(handle); w.width = width; w.height = height; w.resized(); },
        window_set_title(handle, title) { document.title = context.text(title); windowOf(handle).canvas.setAttribute('aria-label', document.title); },
        window_set_fullscreen(handle, fullscreen) { const w = windowOf(handle); w.fullscreen = fullscreen !== 0; w.resized(); return 1; },
        window_focused: () => (document.hasFocus() ? 1 : 0),
        window_display_size(out) {
            const density = globalThis.devicePixelRatio || 1, v = context.view;
            v.setInt32(out, Math.round(screen.width * density), true); v.setInt32(out + 4, Math.round(screen.height * density), true);
            return 1;
        },
        window_key: (handle, key) => (windowOf(handle).keys.has(key) ? 1 : 0),
        window_button: (handle, button) => (windowOf(handle).buttons.has(button) ? 1 : 0),
        window_cursor(handle, out) { const w = windowOf(handle), v = context.view; v.setFloat64(out, w.x, true); v.setFloat64(out + 8, w.y, true); },
        window_set_cursor_mode(handle, mode) {
            const c = windowOf(handle).canvas;
            c.style.cursor = mode === 0x34002 ? 'none' : '';
            if (mode === 0x34003) c.requestPointerLock?.()?.catch?.(() => {});
            else if (document.pointerLockElement === c) document.exitPointerLock();
        },
        window_set_cursor_shape(handle, shape) { windowOf(handle).canvas.style.cursor = CURSORS[shape] ?? ''; return 1; },
        window_set_cursor_image(handle, width, height, rgba, x, y) {
            if (width > 128 || height > 128) return 0;
            const canvas = document.createElement('canvas'); canvas.width = width; canvas.height = height;
            const image = new ImageData(new Uint8ClampedArray(context.bytes.slice(rgba, rgba + width * height * 4)), width, height);
            canvas.getContext('2d').putImageData(image, 0, 0);
            windowOf(handle).canvas.style.cursor = `url(${canvas.toDataURL()}) ${x} ${y}, auto`;
            return 1;
        },
        window_capture_pointer: () => 1, // pointer events already follow captured pointers
        window_text_input(handle, enabled) {
            const w = windowOf(handle);
            w.textEnabled = enabled !== 0;
            if (w.textEnabled) w.textarea.focus({ preventScroll: true }); else w.canvas.focus({ preventScroll: true });
        },
        window_text_rect(handle, x, y, width, height) {
            const w = windowOf(handle), r = w.canvas.getBoundingClientRect();
            Object.assign(w.textarea.style, { left: `${r.left + x}px`, top: `${r.top + y + height}px` });
        },
        clipboard_text: () => (clipboard ? context.allocate(clipboard)[0] : 0),
        clipboard_set_text(text) { clipboard = context.text(text); navigator.clipboard?.writeText(clipboard).catch(() => {}); return 1; },
    };
});
