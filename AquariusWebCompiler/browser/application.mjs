import { module, Scope, num, numeric, unwrap } from "./values.mjs";
import {
  paths,
  bytes,
  checkPixels,
  encodeText,
  decodeText,
  jsonValue,
  parseJson,
  binary,
  parseBinary,
  TextEditor,
  storageKey,
} from "./application-core.mjs";
import { BrowserStorage, BrowserFiles } from "./application-files.mjs";
import { BrowserImages } from "./application-images.mjs";
import {
  BrowserClipboard,
  BrowserServices,
  BrowserFonts,
} from "./application-services.mjs";
import { BrowserWindow } from "./application-window.mjs";
import {
  compress,
  decompress,
  archive,
  readArchive,
} from "./application-serialization.mjs";
export function toValue(value, depth = 0) {
  if (depth > 64) throw new Error("JSON depth exceeded");
  if (numeric(value)) return value.value;
  if (value instanceof Map) {
    const out = {};
    for (const [key, item] of value.values()) {
      if (typeof key !== "string")
        throw new Error("JSON hash keys must be strings");
      Object.defineProperty(out, key, {
        value: toValue(item, depth + 1),
        enumerable: true,
      });
    }
    return out;
  }
  if (Array.isArray(value)) return value.map((v) => toValue(v, depth + 1));
  return value;
}
export function fromValue(value) {
  if (typeof value === "number")
    return num(
      value,
      Number.isInteger(value) && value >= -2147483648 && value <= 2147483647
        ? "int"
        : "double",
    );
  if (Array.isArray(value) || ArrayBuffer.isView(value))
    return Array.from(value, fromValue);
  if (
    value &&
    Object.getPrototypeOf(value) !== null &&
    Object.getPrototypeOf(value) !== Object.prototype
  )
    return value;
  if (value && typeof value === "object")
    return new Map(
      Object.entries(value).map(([k, v]) => ["string:" + k, [k, fromValue(v)]]),
    );
  return value;
}
export class BrowserApplication {
  constructor(host) {
    this.host = host;
    this.storage = new BrowserStorage(host.bundle.entry ?? "application");
    this.images = new BrowserImages();
    this.files = new BrowserFiles(this.storage, (name, blob) =>
      this.download(name, blob),
    );
    this.clipboard = new BrowserClipboard(this.images);
    this.services = new BrowserServices(this.files, this.images);
    this.fonts = new BrowserFonts();
    this.objects = new Map();
    this.streams = new Set();
    this.windows = new Map();
    this.cancellation = null;
    this.disposed = false;
    this.register();
  }
  signal() {
    if (this.disposed) throw new Error("Application runtime disposed");
    return this.cancellation
      ? AbortSignal.any([this.host.signal, this.cancellation.signal])
      : this.host.signal;
  }
  record(value) {
    const m = this.host.newModule();
    for (const [key, v] of Object.entries(value))
      m.scope.create(key, fromValue(v));
    return m;
  }
  own(value) {
    const object = this.objects.get(value);
    if (!object) throw new Error("Expected resource owned by application");
    return object;
  }
  resource(resource) {
    const m = this.record({
      ...resource,
      kind: { File: 0, Directory: 1, Asset: 2 }[resource.kind],
    });
    this.objects.set(m, resource);
    return m;
  }
  image(image) {
    const m = this.record({
      width: image.width,
      height: image.height,
      pixelFormat: "RGBA8",
      alpha: image.alpha ?? "Straight",
      metadata: image.metadata ?? {
        orientation: 1,
        dpiX: null,
        dpiY: null,
        sourcePixelFormat: "RGBA8",
        palette: null,
      },
    });
    this.objects.set(m, image);
    this.bind(m, "Images.Image", "Pixels", () => fromValue(image.pixels));
    return m;
  }
  bind(m, library, name, fn) {
    return this.host.bind(
      m,
      library,
      name,
      async (...args) => {
        try {
          if (!library.startsWith("Tasks")) this.signal().throwIfAborted();
          const value = await fn(...args);
          return value === undefined ? null : value;
        } catch (error) {
          const kinds = {AbortError:"Cancelled",NotAllowedError:"PermissionDenied",SecurityError:"PermissionDenied",NotFoundError:"NotFound",InvalidStateError:"Unavailable",NotReadableError:"Unavailable",QuotaExceededError:"LimitExceeded",NotSupportedError:"Unsupported"};
          const kind = kinds[error.name] ?? (/^(Unsupported|Unavailable|PermissionDenied|Cancelled|InvalidData|LimitExceeded|NotFound|Conflict):/.exec(error.message)?.[1]) ?? "InvalidData";
          throw new Error(`${name}: ${kind}: ${error.message}`);
        }
      },
      true,
    );
  }
  module(name) {
    return this.host.library(name);
  }
  value(v) {
    return toValue(v);
  }
  async download(name, blob) {
    this.host.downloads.set(name, blob);
    if (this.host.frameLimit) return;
    const url = URL.createObjectURL(blob),
      a = document.createElement("a");
    a.href = url;
    a.download = name.split("/").at(-1);
    document.body.append(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  register() {
    const h = this.host,
      b = (m, l, n, f) => this.bind(m, l, n, f),
      files = this.module("Files"),
      owned = (v) => this.own(v),
      data = (v) => bytes(unwrap(v)),
      text = (v) => {
        if (typeof v !== "string") throw new Error("Expected string");
        return v;
      },
      boolean = (v) => {
        if (typeof v !== "boolean") throw new Error("Expected Boolean");
        return v;
      },
      integer = (v) => {
        const n = unwrap(v);
        if (!Number.isInteger(n)) throw new Error("Expected integer");
        return n;
      };
    b(files, "Files", "Capabilities", () =>
      this.record(this.files.capabilities),
    );
    b(files, "Files", "Resolve", (p) =>
      this.resource(this.files.resolve(text(p))),
    );
    b(files, "Files", "Stat", async (r) => {
      const m = await this.files.stat(owned(r), this.signal());
      return this.record({ ...m, kind: m.kind === "Directory" ? 1 : 0 });
    });
    b(files, "Files", "ReadBytes", async (r) =>
      fromValue(await this.files.read(owned(r), this.signal())),
    );
    b(files, "Files", "ReadText", async (r, e) =>
      decodeText(await this.files.read(owned(r), this.signal()), text(e)),
    );
    b(
      files,
      "Files",
      "SaveBytes",
      async (r, d, a = false) =>
        await this.files.replace(owned(r), data(d), boolean(a), this.signal()),
    );
    b(
      files,
      "Files",
      "SaveText",
      async (r, t, e, a = false) =>
        await this.files.replace(
          owned(r),
          encodeText(text(t), text(e)),
          boolean(a),
          this.signal(),
        ),
    );
    b(files, "Files", "Enumerate", async (r) =>
      (await this.files.enumerate(owned(r), this.signal())).map((r) =>
        this.resource(r),
      ),
    );
    b(files, "Files", "CreateDirectory", async (r) => {
      await this.files.directory(owned(r), this.signal());
    });
    b(files, "Files", "Delete", (r, recursive = false) =>
      this.files.remove(owned(r), boolean(recursive), this.signal()),
    );
    b(files, "Files", "Copy", async (a, d, o = false) => {
      await this.files.copy(owned(a), owned(d), boolean(o), this.signal());
    });
    b(files, "Files", "Move", async (a, d, o = false) => {
      await this.files.move(owned(a), owned(d), boolean(o), this.signal());
    });
    b(files, "Files", "Temporary", (directory) => {
      const r = this.files.temporaryResource(boolean(directory)),
        m = this.resource(r);
      b(m, "Files.Resource", "Dispose", async () => {
        await this.files.remove(r, true);
        this.objects.delete(m);
      });
      return m;
    });
    b(files, "Files", "Open", async (r, mode) => {
      const stream = await this.files.open(owned(r), text(mode), this.signal());
      this.streams.add(stream);
      const m = this.record({
        canRead: stream.canRead,
        canWrite: stream.canWrite,
        canSeek: stream.canSeek,
      });
      for (const name of ["Read", "Write", "Seek", "Flush", "Close"])
        b(m, "Files.Stream", name, async (...args) =>
          fromValue(
            await stream[name](
              ...(name === "Write" ? [data(args[0])] : args.map(unwrap)),
            ),
          ),
        );
      return m;
    });
    const path = this.module("Paths");
    for (const [name, fn] of Object.entries(paths))
      b(path, "Paths", name, (...args) => fn(...args.map(unwrap)));
    const images = this.module("Images");
    b(images, "Images", "Formats", () => ["Png", "Jpeg", "Bmp", "Gif", "Tiff"]);
    b(images, "Images", "Create", (w, hh, p) => {
      w = integer(w);
      hh = integer(hh);
      checkPixels(w, hh);
      const pixels = data(p);
      if (pixels.length !== w * hh * 4) throw new Error("RGBA size mismatch");
      return this.image({ width: w, height: hh, pixels, alpha: "Straight" });
    });
    b(images, "Images", "Inspect", async (d) => {
      const info = await this.images.inspect(data(d), this.signal());
      return this.record({
        ...info,
        format: ["Png", "Jpeg", "Bmp", "Gif", "Tiff"].indexOf(info.format),
      });
    });
    b(images, "Images", "Decode", async (d, f) =>
      this.image(await this.images.decode(data(d), integer(f), this.signal())),
    );
    b(images, "Images", "Load", async (r, f) =>
      this.image(
        await this.images.decode(
          await this.files.read(owned(r), this.signal()),
          integer(f),
          this.signal(),
        ),
      ),
    );
    b(images, "Images", "Encode", async (i, f, o) =>
      fromValue(
        await this.images.encode(
          owned(i),
          canonicalFormat(text(f)),
          this.value(o),
          this.signal(),
        ),
      ),
    );
    b(images, "Images", "Save", async (r, i, f, o) =>
      this.files.replace(
        owned(r),
        await this.images.encode(
          owned(i),
          canonicalFormat(text(f)),
          this.value(o),
          this.signal(),
        ),
        false,
        this.signal(),
      ),
    );
    this.registerData(b, data, text, integer);
    this.registerEditing(b, text, integer, boolean);
    this.registerHost(b, text, integer);
  }
  registerData(b, data, text, integer) {
    const m = this.module("Serialization");
    b(m, "Serialization", "Json", (v) =>
      JSON.stringify(jsonValue(this.value(v))),
    );
    b(m, "Serialization", "ParseJson", (v) => fromValue(parseJson(text(v))));
    b(m, "Serialization", "Binary", (v, s) =>
      fromValue(binary(this.value(v), integer(s))),
    );
    b(m, "Serialization", "ParseBinary", (d) => {
      const result = parseBinary(data(d)),
        r = this.record({ schema: result.schema });
      r.scope.create("value", fromValue(result.value));
      return r;
    });
    b(m, "Serialization", "Compress", (d, f, l = "Optimal") =>
      fromValue(compress(data(d), text(f), text(l))),
    );
    b(m, "Serialization", "Decompress", async (d, f) =>
      fromValue(await decompress(data(d), text(f), this.signal())),
    );
    b(m, "Serialization", "Archive", (entries) => {
      if (!(entries instanceof Map)) throw new Error("Expected resource hash");
      const out = Object.create(null);
      for (const [name, value] of entries.values())
        out[text(name)] = data(value);
      return fromValue(archive(out));
    });
    b(m, "Serialization", "ReadArchive", async (d) =>
      fromValue(await readArchive(data(d), this.signal())),
    );
    const storage = this.module("Storage"),
      load = async (key) => {
        const bytes = await this.storage.read(text(key), this.signal());
        if (!bytes) return null;
        const doc = parseBinary(bytes);
        if (doc.schema !== 1)
          throw new Error("Unsupported: Settings schema requires migration");
        return doc.value;
      };
    b(storage, "Storage", "SettingsDirectory", () =>
      this.resource(this.files.resolve("persistent:/settings")),
    );
    b(storage, "Storage", "DataDirectory", () =>
      this.resource(this.files.resolve("persistent:/data")),
    );
    b(storage, "Storage", "Read", async (k) =>
      fromValue(await this.storage.read(text(k), this.signal())),
    );
    b(storage, "Storage", "Write", async (k, d) => {
      await this.storage.write(text(k), data(d), this.signal());
    });
    b(storage, "Storage", "Delete", (k) =>
      this.storage.remove(text(k), this.signal()),
    );
    b(storage, "Storage", "GetPreference", async (k) =>
      fromValue(await load(k)),
    );
    b(storage, "Storage", "SetPreference", async (k, v) => {
      await this.storage.write(
        text(k),
        binary(this.value(v), 1),
        this.signal(),
      );
    });
    b(storage, "Storage", "Recent", async () =>
      fromValue((await load("recent")) ?? []),
    );
    b(storage, "Storage", "Remember", async (r) => {
      const resource = this.own(r),
        recent = (await load("recent")) ?? [];
      await this.storage.write(
        "recent",
        binary(
          [
            { ...resource, kind: resource.kind === "Directory" ? 1 : 0 },
            ...recent.filter((v) =>
              resource.persistentIdentity
                ? v.persistentIdentity !== resource.persistentIdentity
                : v.provider !== resource.provider || v.id !== resource.id,
            ),
          ].slice(0, 20),
          1,
        ),
        this.signal(),
      );
    });
  }
  registerEditing(b, text, integer, boolean) {
    const clipboard = this.module("Clipboard");
    b(clipboard, "Clipboard", "Capabilities", () =>
      this.record(this.clipboard.capabilities),
    );
    b(clipboard, "Clipboard", "Formats", () =>
      this.clipboard.formats(this.signal()),
    );
    b(
      clipboard,
      "Clipboard",
      "ReadText",
      async () => (await this.clipboard.read(this.signal())).text,
    );
    b(clipboard, "Clipboard", "ReadImage", async () => {
      const image = (await this.clipboard.read(this.signal())).image;
      return image ? this.image(image) : null;
    });
    b(clipboard, "Clipboard", "WriteText", (t) =>
      this.clipboard.write({ text: text(t), image: null }, this.signal()),
    );
    b(clipboard, "Clipboard", "WriteImage", (i, t = null) =>
      this.clipboard.write(
        { text: t === null ? null : text(t), image: this.own(i) },
        this.signal(),
      ),
    );
    const fonts = this.module("Fonts");
    b(fonts, "Fonts", "Enumerate", async () =>
      (await this.fonts.enumerate(this.signal())).map((f) => this.record(f)),
    );
    b(fonts, "Fonts", "Available", (f) => this.fonts.available(text(f)));
    b(fonts, "Fonts", "Measure", (t, f, s) =>
      this.record(this.fonts.measure(text(t), text(f), unwrap(s))),
    );
    b(fonts, "Fonts", "Selection", (t, a, e, f, s) =>
      this.fonts
        .selection(text(t), integer(a), integer(e), text(f), unwrap(s))
        .map((r) => this.record(r)),
    );
    const edit = this.module("TextEdit");
    b(edit, "TextEdit", "Convert", (t, p, f, to) =>
      num(
        new TextEditor(text(t)).convert(integer(p), text(f), text(to)),
        "int",
      ),
    );
    b(edit, "TextEdit", "Create", (t) => {
      const editor = new TextEditor(text(t)),
        m = this.record({ positionUnit: "grapheme" }),
        bind = (n, f) => b(m, "TextEdit.Editor", n, f),
        copy = async (cut) => {
          if (editor.start === editor.end) return;
          await this.clipboard.write(
            { text: editor.selectedText, image: null },
            this.signal(),
          );
          if (cut) editor.insert("");
        },
        paste = async () => {
          const { text } = await this.clipboard.read(this.signal());
          if (text !== null) editor.commit(text);
        };
      bind("State", () => this.record(editor.state()));
      bind("Select", (a, c) => editor.select(integer(a), integer(c)));
      bind("Insert", (t) => editor.insert(text(t)));
      bind("Backspace", () => editor.backspace());
      bind("Delete", () => editor.delete());
      bind("Move", (d, u, e) => editor.move(integer(d), text(u), boolean(e)));
      bind("Convert", (p, f, t) =>
        num(editor.convert(integer(p), text(f), text(t)), "int"),
      );
      bind("Composition", (t, c, unit = "UnicodeScalar") =>
        editor.setComposition(text(t), integer(c), text(unit)),
      );
      bind("CancelComposition", () => (editor.composition = null));
      bind("Commit", (t) => editor.commit(text(t)));
      bind("Copy", () => copy(false));
      bind("Cut", () => copy(true));
      bind("Paste", paste);
      bind("Shortcut", async (k, mods) => {
        k = text(k);
        mods = integer(mods);
        if (editor.composition) return false;
        const command = !!(mods & 10),
          extend = !!(mods & 1);
        if (command) {
          switch (k.toLowerCase()) {
            case "a":
              editor.select(0, editor.length);
              return true;
            case "c":
              await copy(false);
              return true;
            case "x":
              await copy(true);
              return true;
            case "v":
              await paste();
              return true;
          }
        }
        switch (k) {
          case "ArrowLeft":
            editor.move(-1, command ? "word" : "grapheme", extend);
            return true;
          case "ArrowRight":
            editor.move(1, command ? "word" : "grapheme", extend);
            return true;
          case "Home":
            editor.move(-1, "line", extend);
            return true;
          case "End":
            editor.move(1, "line", extend);
            return true;
          case "Backspace":
            editor.backspace();
            return true;
          case "Delete":
            editor.delete();
            return true;
        }
        return false;
      });
      return m;
    });
  }
  registerHost(b, text, integer) {
    const app = this.module("Application");
    b(app, "Application", "Capabilities", () =>
      this.record(this.services.capabilities),
    );
    b(app, "Application", "LaunchFiles", () =>
      this.services.launchFiles.map((r) => this.resource(r)),
    );
    b(app, "Application", "OpenFiles", async (o) =>
      (await this.services.openFiles(this.value(o), this.signal())).map((r) =>
        this.resource(r),
      ),
    );
    b(app, "Application", "SaveFile", async (o) => {
      const r = await this.services.saveFile(this.value(o), this.signal());
      return r ? this.resource(r) : null;
    });
    b(app, "Application", "PickDirectory", async () => {
      const r = await this.services.pickDirectory(this.signal());
      return r ? this.resource(r) : null;
    });
    b(app, "Application", "Print", (i) =>
      this.services.printImage(this.own(i), this.signal()),
    );
    b(app, "Application", "AcquireImage", async () => {
      const i = await this.services.acquireImage(this.signal());
      return i ? this.image(i) : null;
    });
    b(app, "Application", "Accessibility", (nodes) =>
      this.services.updateAccessibility(this.value(nodes)),
    );
    b(app, "Application", "RegisterFileAssociation", () => {
      throw new Error(
        "Unsupported: Browser file handlers require a web app manifest and installation",
      );
    });
    const tasks = this.module("Tasks");
    b(tasks, "Tasks", "CreateCancellation", () => {
      const source = new AbortController(),
        m = this.record({});
      this.objects.set(m, source);
      b(m, "Tasks.Cancellation", "Cancel", () =>
        source.abort(new DOMException("Cancelled", "AbortError")),
      );
      b(m, "Tasks.Cancellation", "IsCancelled", () => source.signal.aborted);
      b(m, "Tasks.Cancellation", "Dispose", () => { this.objects.delete(m); });
      return m;
    });
    b(tasks, "Tasks", "UseCancellation", (source) => {
      const controller = source === null ? null : this.own(source);
      if (controller !== null && !(controller instanceof AbortController))
        throw new Error("Expected an application cancellation token");
      this.cancellation = controller;
    });
    const windows = this.module("Window");
    b(windows, "Window", "Attach", (window) => this.window(window.canvas));
    b(windows, "Window", "Current", () =>
      this.window(this.host.processing.dom),
    );
    b(windows, "Window", "FromProcessingImage", async (image) => {
      const i = this.host.objects.get(image);
      if (!i || i.disposed || (!i.bytes && !i.target))
        throw new Error("Expected a live Processing image or canvas");
      const pixels = i.bytes ? i.bytes.slice() : await i.target.readPixels();
      this.signal().throwIfAborted();
      return this.image({
        width: i.bytes ? i.width : i.pixelWidth,
        height: i.bytes ? i.height : i.pixelHeight,
        pixels,
        alpha: "Straight",
      });
    });
    b(windows, "Window", "ToProcessingImage", (image) => {
      const i = this.own(image);
      return this.host.processing.image(i.width, i.height, i.pixels.slice())
        .module;
    });
  }
  window(canvas) {
    if (!canvas) throw new Error("Create a graphics window first");
    if (this.windows.has(canvas)) return this.windows.get(canvas).module;
    const adapter = new BrowserWindow(canvas, this.files),
      m = this.record({}),
      b = (n, f) => this.bind(m, "Window.Integration", n, f);
    this.windows.set(canvas, { adapter, module: m });
    b("Capabilities", () => this.record(adapter.capabilities));
    b("SetTitle", (t) => adapter.setTitle(t));
    b("SetCursor", (s) => adapter.setCursor(s));
    b("SetCustomCursor", (i, x, y) =>
      adapter.setCustomCursor(
        this.own(i),
        unwrap(x),
        unwrap(y),
        this.images,
        this.signal(),
      ),
    );
    b("CapturePointer", (id) => adapter.capturePointer(unwrap(id)));
    b("ReleasePointer", (id) => adapter.releasePointer(unwrap(id)));
    b("ResolveClose", (d) => adapter.resolveClose(d));
    b("Poll", () =>
      adapter.poll().map((e) => {
        const record = this.record(e);
        if (e.files)
          record.scope.create(
            "files",
            e.files.map((r) => this.resource(r)),
          );
        return record;
      }),
    );
    return m;
  }
  async dispose() {
    if (this.disposed) return;
    this.images.dispose();
    this.services.dispose();
    for (const { adapter } of this.windows.values()) adapter.dispose();
    const errors = [];
    for (const stream of this.streams)
      try {
        if (this.host.signal.aborted || this.cancellation?.signal.aborted)
          stream.Abort();
        else await stream.Close();
      } catch (e) {
        errors.push(e);
      }
    this.files.dispose();
    await this.storage.dispose();
    this.objects.clear();
    this.disposed = true;
    if (errors.length)
      throw new AggregateError(errors, "Application resource cleanup failed");
  }
}
function canonicalFormat(value) {
  const format = ["Png", "Jpeg", "Bmp", "Gif", "Tiff"].find(
    (f) => f.toLowerCase() === value.toLowerCase(),
  );
  if (!format) throw new Error("Unsupported codec");
  return format;
}
