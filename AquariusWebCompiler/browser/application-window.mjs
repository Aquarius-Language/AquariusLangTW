import { capability } from "./application-services.mjs";
import { checkPixels } from "./application-core.mjs";
export class BrowserWindow {
  constructor(canvas, files) {
    this.canvas = canvas;
    this.files = files;
    this.queue = [];
    this.abort = new AbortController();
    this.disposed = false;
    this.cursorUrl = null;
    this.preventClose = false;
    this.overflow = false;
    this.input();
  }
  get capabilities() {
    return {
      events: capability(true),
      fileDrop: capability(true),
      closeInterception: capability(
        false,
        "beforeunload only requests a browser confirmation; asynchronous defer is unavailable",
      ),
      customCursor: capability(true, "Browser may impose cursor dimensions"),
      capture: capability(
        !!this.canvas.setPointerCapture,
        "An active pointer is required",
      ),
      touch: capability(!!globalThis.PointerEvent),
      pen: capability(
        !!globalThis.PointerEvent,
        "Optional properties are provided by PointerEvent",
      ),
    };
  }
  event(type, fields = {}) {
    if (this.queue.length >= 4096) {
      this.overflow = true;
      return;
    }
    this.queue.push({
      type,
      timestamp: performance.now() / 1000,
      logicalKey: null,
      physicalKey: null,
      modifiers: 0,
      deviceId: null,
      device: null,
      button: null,
      wheelX: null,
      wheelY: null,
      wheelUnit: null,
      samples: null,
      contact: null,
      eraser: null,
      files: null,
      state: null,
      ...fields,
    });
  }
  input() {
    const signal = this.abort.signal,
      on = (target, type, fn, options = {}) =>
        target.addEventListener(type, fn, { signal, ...options }),
      modifiers = (e) =>
        (e.shiftKey ? 1 : 0) |
        (e.ctrlKey ? 2 : 0) |
        (e.altKey ? 4 : 0) |
        (e.metaKey ? 8 : 0) |
        (e.getModifierState?.("CapsLock") ? 16 : 0) |
        (e.getModifierState?.("NumLock") ? 32 : 0);
    for (const type of ["keydown", "keyup"])
      on(window, type, (e) => {
        if (
          e.target !== this.canvas &&
          e.target !==
            document.querySelector('textarea[aria-label="Aquarius text input"]')
        )
          return;
        this.event(
          type === "keyup"
            ? "keyReleased"
            : e.repeat
              ? "keyRepeat"
              : "keyPressed",
          {
            timestamp: e.timeStamp / 1000,
            logicalKey: e.key,
            physicalKey: e.code || null,
            modifiers: modifiers(e),
          },
        );
      });
    for (const [type, name] of [
      ["pointerdown", "pointerPressed"],
      ["pointerup", "pointerReleased"],
      ["pointermove", "pointerMoved"],
      ["pointercancel", "inputCancelled"],
      ["lostpointercapture", "captureLost"],
    ])
      on(this.canvas, type, (e) => {
        const bounds = this.canvas.getBoundingClientRect(),
          sample = (p) => ({
            x: p.clientX - bounds.left,
            y: p.clientY - bounds.top,
            timestamp: p.timeStamp / 1000,
            pressure:
              p.pointerType === "pen" || p.pointerType === "touch"
                ? p.pressure
                : null,
            tiltX: p.pointerType === "pen" ? p.tiltX : null,
            tiltY: p.pointerType === "pen" ? p.tiltY : null,
          });
        let events = e.getCoalescedEvents?.() ?? [];
        if (!events.length || events.at(-1).timeStamp !== e.timeStamp)
          events = [...events, e];
        this.event(name, {
          timestamp: e.timeStamp / 1000,
          deviceId: e.pointerId,
          device: e.pointerType || null,
          button: e.button,
          modifiers: modifiers(e),
          samples: events.map(sample).sort((a, b) => a.timestamp - b.timestamp),
          contact:
            type !== "pointercancel" && type !== "pointerup" && e.buttons !== 0,
          eraser:
            e.pointerType === "pen"
              ? e.button === 5 || !!(e.buttons & 32)
              : null,
        });
      });
    on(this.canvas, "wheel", (e) =>
      this.event("wheel", {
        timestamp: e.timeStamp / 1000,
        wheelX: e.deltaX,
        wheelY: e.deltaY,
        wheelUnit: ["pixel", "line", "page"][e.deltaMode],
        modifiers: modifiers(e),
      }),
    );
    on(this.canvas, "dragover", (e) => {
      if (Array.from(e.dataTransfer.types).includes("Files"))
        e.preventDefault();
    });
    on(this.canvas, "drop", (e) => {
      e.preventDefault();
      this.event("fileDrop", {
        files: Array.from(e.dataTransfer.files, (f) =>
          this.files.register(f, f.name),
        ),
      });
    });
    on(window, "blur", () => {
      this.event("focus", { state: false });
      this.event("inputCancelled");
      this.event("activation", { state: false });
    });
    on(window, "focus", () => {
      this.event("focus", { state: true });
      this.event("activation", { state: true });
    });
    on(document, "visibilitychange", () =>
      this.event("visibility", { state: !document.hidden }),
    );
    on(window, "pagehide", () => this.event("hidden"));
    on(window, "pageshow", () => this.event("shown"));
    on(window, "beforeunload", (e) => {
      this.event("closeRequested");
      if (this.preventClose) {
        e.preventDefault();
        e.returnValue = "";
      }
    });
    this.event("created");
    this.event("visibility", { state: !document.hidden });
  }
  live() {
    if (this.disposed || !this.canvas.isConnected)
      throw new Error("Window has been destroyed");
  }
  poll() {
    if (!this.canvas.isConnected && !this.disposed) this.dispose();
    if (this.overflow) {
      this.queue = [];
      this.overflow = false;
      throw new Error("LimitExceeded: Window event queue overflow");
    }
    return this.queue.splice(0);
  }
  setTitle(title) {
    this.live();
    if (typeof title !== "string") throw new Error("Expected title");
    document.title = title;
    this.canvas.setAttribute("aria-label", title);
  }
  setCursor(shape) {
    this.live();
    const map = {
      Arrow: "default",
      Text: "text",
      Crosshair: "crosshair",
      Hand: "pointer",
      ResizeHorizontal: "ew-resize",
      ResizeVertical: "ns-resize",
      ResizeDiagonal1: "nwse-resize",
      ResizeDiagonal2: "nesw-resize",
      Move: "move",
      NotAllowed: "not-allowed",
    };
    if (!map[shape]) throw new Error("Invalid cursor");
    this.canvas.style.cursor = map[shape];
    if (this.cursorUrl) URL.revokeObjectURL(this.cursorUrl);
    this.cursorUrl = null;
  }
  async setCustomCursor(image, x, y, images, signal) {
    this.live();
    checkPixels(image.width, image.height);
    if (
      ![x, y].every(Number.isInteger) ||
      x < 0 ||
      y < 0 ||
      x >= image.width ||
      y >= image.height
    )
      throw new Error("Invalid cursor hotspot");
    if (image.width > 128 || image.height > 128)
      throw new Error("Unsupported: Browser cursor exceeds 128 pixels");
    const bytes = await images.encode(image, "Png", {}, signal);
    this.live();
    if (this.cursorUrl) URL.revokeObjectURL(this.cursorUrl);
    this.cursorUrl = URL.createObjectURL(
      new Blob([bytes], { type: "image/png" }),
    );
    this.canvas.style.cursor = `url("${this.cursorUrl}") ${x} ${y}, auto`;
  }
  capturePointer(id) {
    this.live();
    this.canvas.setPointerCapture(id);
  }
  releasePointer(id) {
    this.live();
    this.canvas.releasePointerCapture(id);
  }
  resolveClose(decision) {
    this.live();
    if (decision === "Defer")
      throw new Error("Unsupported: Browser cannot defer page closure");
    if (!["Accept", "Cancel"].includes(decision))
      throw new Error("Invalid close decision");
    this.preventClose = decision === "Cancel";
  }
  dispose() {
    if (this.disposed) return;
    this.event("destroyed");
    this.abort.abort();
    if (this.cursorUrl) URL.revokeObjectURL(this.cursorUrl);
    this.disposed = true;
  }
}
