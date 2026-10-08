import {
  checkPixels,
  checkBytes,
  strictText,
  TextEditor,
} from "./application-core.mjs";
export const capability = (available, restriction = null) => ({
  available,
  restriction,
});
function ready(signal) {
  signal?.throwIfAborted();
  if (!globalThis.isSecureContext)
    throw new Error("PermissionDenied: Secure context required");
  if (!document.hasFocus())
    throw new Error("PermissionDenied: Focus the application first");
}
export class BrowserClipboard {
  constructor(images) {
    this.images = images;
  }
  get capabilities() {
    return {
      text: capability(
        !!navigator.clipboard,
        "Secure context, focus and browser permission required",
      ),
      image: capability(
        !!(
          navigator.clipboard?.read &&
          navigator.clipboard?.write &&
          globalThis.ClipboardItem
        ),
        "PNG clipboard support depends on browser permissions and activation",
      ),
      multiple: capability(!!(navigator.clipboard?.write && globalThis.ClipboardItem)),
    };
  }
  async formats(signal) {
    ready(signal);
    if (!navigator.clipboard)
      throw new Error("Unsupported: Clipboard API unavailable");
    if (!navigator.clipboard.read)
      throw new Error("Unsupported: Browser cannot query clipboard formats");
    const items = await navigator.clipboard.read();
    signal?.throwIfAborted();
    return [...new Set(items.flatMap((i) => i.types))];
  }
  async read(signal) {
    ready(signal);
    if (!navigator.clipboard)
      throw new Error("Unsupported: Clipboard API unavailable");
    if (!navigator.clipboard.read) {
      const text = await navigator.clipboard.readText();
      signal?.throwIfAborted();
      return { text, image: null };
    }
    const items = await navigator.clipboard.read();
    let text = null,
      image = null;
    for (const item of items) {
      if (text === null && item.types.includes("text/plain")) {
        const blob = await item.getType("text/plain");
        checkBytes(blob.size);
        text = await blob.text();
      }
      if (image === null && item.types.includes("image/png")) {
        const blob = await item.getType("image/png");
        checkBytes(blob.size);
        image = await this.images.decode(
          new Uint8Array(await blob.arrayBuffer()),
          0,
          signal,
        );
      }
    }
    signal?.throwIfAborted();
    return { text, image };
  }
  async write({ text = null, image = null }, signal) {
    const content = { text, image };
    ready(signal);
    if (!navigator.clipboard)
      throw new Error("Unsupported: Clipboard API unavailable");
    if (content.text === null && content.image === null)
      throw new Error("Empty clipboard content");
    if (content.image) {
      if (
        !navigator.clipboard.write ||
        !globalThis.ClipboardItem ||
        (ClipboardItem.supports && !ClipboardItem.supports("image/png"))
      )
        throw new Error("Unsupported: PNG clipboard unavailable");
      const representations = {
        "image/png": this.images
          .encode(content.image, "Png", {}, signal)
          .then((b) => new Blob([b], { type: "image/png" })),
      };
      if (content.text !== null)
        representations["text/plain"] = new Blob([strictText(content.text)], {
          type: "text/plain",
        });
      await navigator.clipboard.write([new ClipboardItem(representations)]);
    } else await navigator.clipboard.writeText(strictText(content.text));
    signal?.throwIfAborted();
  }
}
function pickerTypes(options) {
  return (options.filters ?? []).map((filter) => ({
    description: filter.name,
    accept: {
      "application/octet-stream": filter.extensions.map((e) => {
        if (!/^\.?[a-zA-Z0-9]+$/.test(e))
          throw new Error("Invalid filename extension");
        return "." + e.replace(/^\./, "");
      }),
    },
  }));
}
export class BrowserServices {
  constructor(files, images) {
    this.files = files;
    this.images = images;
    this.launchFiles = [];
    this.accessibility = null;
    this.pending = new Set();
    if (globalThis.launchQueue)
      launchQueue.setConsumer((params) => {
        this.launchFiles = params.files.map((handle) =>
          files.register(handle, handle.name),
        );
      });
  }
  get capabilities() {
    return {
      files: capability(
        true,
        "Selected resources, downloads and scoped persistent storage",
      ),
      codecs: capability(!!globalThis.Worker),
      persistentStorage: capability(
        !!globalThis.indexedDB,
        "Origin quota and eviction apply",
      ),
      pickers: capability(
        true,
        "User activation required; input/download fallbacks",
      ),
      directoryPicker: capability(!!globalThis.showDirectoryPicker),
      printing: capability(
        !!globalThis.print,
        "Browser owns dialog; completion/cancellation cannot be confirmed",
      ),
      imageAcquisition: capability(
        true,
        "Image picker; mobile browsers may offer camera capture",
      ),
      fontEnumeration: capability(
        !!globalThis.queryLocalFonts,
        "Local font permission required",
      ),
      accessibility: capability(
        true,
        "Semantic DOM mirror of application-owned state",
      ),
      fileAssociations: capability(
        false,
        "Installable web app manifest/OS installation must declare associations",
      ),
      activation: capability(
        !!globalThis.launchQueue,
        "Requires installed web app with file handlers",
      ),
    };
  }
  async openFiles(options = {}, signal) {
    ready(signal);
    if (globalThis.showOpenFilePicker) {
      try {
        const handles = await showOpenFilePicker({
          multiple: !!options.multiple,
          types: pickerTypes(options),
        });
        signal?.throwIfAborted();
        return handles.map((h) => this.files.register(h, h.name));
      } catch (e) {
        if (e.name === "AbortError" && !signal?.aborted) return [];
        throw e;
      }
    }
    return this.inputFiles(options, signal);
  }
  inputFiles(options = {}, signal) {
    ready(signal);
    return new Promise((resolve, reject) => {
      const input = document.createElement("input");
      input.type = "file";
      input.multiple = !!options.multiple;
      input.accept = (options.filters ?? [])
        .flatMap((f) => f.extensions.map((e) => "." + e.replace(/^\./, "")))
        .join(",");
      if (options.capture) input.setAttribute("capture", "environment");
      input.style.display = "none";
      document.body.append(input);
      const dispose = () => {
          input.remove();
          signal?.removeEventListener("abort", abort);
          this.pending.delete(abort);
        },
        abort = () => {
          dispose();
          reject(signal?.reason ?? new DOMException("Cancelled", "AbortError"));
        };
      this.pending.add(abort);
      input.addEventListener(
        "change",
        () => {
          const result = Array.from(input.files ?? [], (f) =>
            this.files.register(f, f.name),
          );
          dispose();
          resolve(result);
        },
        { once: true },
      );
      input.addEventListener(
        "cancel",
        () => {
          dispose();
          resolve([]);
        },
        { once: true },
      );
      signal?.addEventListener("abort", abort, { once: true });
      input.click();
    });
  }
  async saveFile(options = {}, signal) {
    ready(signal);
    if (globalThis.showSaveFilePicker)
      try {
        const handle = await showSaveFilePicker({
          suggestedName: options.suggestedName || "document.aqd",
          types: pickerTypes(options),
        });
        signal?.throwIfAborted();
        return this.files.register(handle, handle.name);
      } catch (e) {
        if (e.name === "AbortError" && !signal?.aborted) return null;
        throw e;
      }
    return this.files.resolve(
      "download:/" + (options.suggestedName || "document.aqd"),
    );
  }
  async pickDirectory(signal) {
    ready(signal);
    if (!globalThis.showDirectoryPicker)
      throw new Error("Unsupported: Directory picker unavailable");
    try {
      const handle = await showDirectoryPicker();
      signal?.throwIfAborted();
      return this.files.register(handle, handle.name, "Directory");
    } catch (e) {
      if (e.name === "AbortError" && !signal?.aborted) return null;
      throw e;
    }
  }
  async acquireImage(signal) {
    const resources = await this.inputFiles(
      {
        filters: [
          {
            name: "Images",
            extensions: ["png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff"],
          },
        ],
        capture: true,
      },
      signal,
    );
    return resources.length
      ? this.images.decode(
          await this.files.read(resources[0], signal),
          0,
          signal,
        )
      : null;
  }
  async printImage(image, signal) {
    signal?.throwIfAborted();
    checkPixels(image.width, image.height);
    const data = await this.images.encode(image, "Png", {}, signal),
      url = URL.createObjectURL(new Blob([data], { type: "image/png" })),
      frame = document.createElement("iframe");
    frame.title = "Aquarius print document";
    frame.style.display = "none";
    try {
      await new Promise((resolve, reject) => {
        frame.onload = resolve;
        frame.onerror = reject;
        frame.srcdoc = `<!doctype html><title>Aquarius document</title><style>@page{margin:12mm}img{max-width:100%;max-height:95vh}</style><img src="${url}" alt="Document">`;
        document.body.append(frame);
      });
      await Promise.all(
        Array.from(frame.contentDocument.images, (i) => i.decode()),
      );
      signal?.throwIfAborted();
      frame.contentWindow.focus();
      frame.contentWindow.print();
    } finally {
      frame.remove();
      URL.revokeObjectURL(url);
    }
  }
  updateAccessibility(nodes) {
    if (!Array.isArray(nodes) || nodes.length > 4096)
      throw new Error("Invalid accessibility node list");
    const ids = new Set(),
      fragment = document.createDocumentFragment();
    for (const node of nodes) {
      if (
        typeof node.id !== "string" ||
        !node.id ||
        ids.has(node.id) ||
        typeof node.role !== "string" ||
        typeof node.label !== "string"
      )
        throw new Error("Invalid accessibility node");
      ids.add(node.id);
      const item = document.createElement("div");
      item.dataset.aquariusId = node.id;
      item.setAttribute("role", node.role);
      item.setAttribute("aria-label", node.label);
      if (node.value != null)
        item.setAttribute("aria-valuetext", String(node.value));
      if (node.disabled) item.setAttribute("aria-disabled", "true");
      fragment.append(item);
    }
    if (!this.accessibility) {
      this.accessibility = document.createElement("div");
      this.accessibility.setAttribute("aria-label", "Aquarius application");
      Object.assign(this.accessibility.style, {
        position: "fixed",
        width: "1px",
        height: "1px",
        overflow: "hidden",
        clipPath: "inset(50%)",
      });
      document.body.append(this.accessibility);
    }
    this.accessibility.replaceChildren(fragment);
  }
  dispose() {
    for (const abort of this.pending) abort();
    this.accessibility?.remove();
  }
}
export class BrowserFonts {
  async enumerate(signal) {
    ready(signal);
    if (!globalThis.queryLocalFonts)
      throw new Error("Unsupported: Local font enumeration unavailable");
    const fonts = await queryLocalFonts();
    signal?.throwIfAborted();
    return fonts.map((f) => ({ family: f.family, style: f.style }));
  }
  context(family, size, fallback = null) {
    if (
      typeof family !== "string" ||
      !Number.isFinite(size) ||
      size <= 0 ||
      size > 512
    )
      throw new Error("Invalid font request");
    const c = document.createElement("canvas").getContext("2d");
    const name = ["sans-serif", "serif", "monospace"].includes(family)
      ? family
      : JSON.stringify(family);
    c.font = `${size}px ${name}${fallback ? "," + fallback : ""}`;
    return c;
  }
  measure(text, family, size) {
    strictText(text);
    if (text.length > 4096) throw new Error("Text measurement limit");
    const m = this.context(family, size).measureText(text);
    return {
      x: 0,
      y: 0,
      width: m.width,
      height: m.actualBoundingBoxAscent + m.actualBoundingBoxDescent,
    };
  }
  available(family) {
    if (["sans-serif", "serif", "monospace"].includes(family)) return true;
    const sample = "mmmmmmwwWW漢字",
      measure = (f) => this.context(f, 48).measureText(sample).width;
    return ["monospace", "serif", "sans-serif"].some(
      (f) =>
        this.context(family, 48, f).measureText(sample).width !== measure(f),
    );
  }
  selection(text, start, end, family, size) {
    const editor = new TextEditor(text);
    editor.convert(start, "utf16", "grapheme");
    editor.convert(end, "utf16", "grapheme");
    if (end < start) throw new Error("Invalid selection");
    const result = [];
    let offset = 0,
      lineIndex = 0;
    for (const line of text.split("\n")) {
      const a = Math.max(0, Math.min(line.length, start - offset)),
        b = Math.max(0, Math.min(line.length, end - offset));
      if (start <= offset + line.length && end >= offset) {
        const x = this.measure(line.slice(0, a), family, size).width;
        result.push({
          x,
          y: lineIndex * size * 1.2,
          width: this.measure(line.slice(0, b), family, size).width - x,
          height: size,
        });
      }
      offset += line.length + 1;
      lineIndex++;
    }
    return result;
  }
}
