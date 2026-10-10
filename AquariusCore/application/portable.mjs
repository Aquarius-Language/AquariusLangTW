// Platform-independent counterpart of the C# application contracts. No DOM or storage APIs.
export const limits = Object.freeze({
  maxBytes: 64 * 1024 * 1024,
  maxDimension: 8192,
  maxPixels: 16 * 1024 * 1024,
  maxFrames: 256,
  maxEntries: 4096,
  maxDepth: 64,
});
export function checkBytes(n) {
  if (!Number.isSafeInteger(n) || n < 0 || n > limits.maxBytes)
    throw new Error("LimitExceeded: Data exceeds the byte limit");
}
export function checkPixels(w, h, frames = 1) {
  if (
    ![w, h, frames].every(Number.isInteger) ||
    w < 1 ||
    h < 1 ||
    w > limits.maxDimension ||
    h > limits.maxDimension ||
    frames < 1 ||
    frames > limits.maxFrames ||
    w * h * frames > limits.maxPixels
  )
    throw new Error(
      "LimitExceeded: Image dimensions or frames exceed the limit",
    );
  checkBytes(w * h * frames * 4);
}
export function bytes(values) {
  if (!(values instanceof Uint8Array) && !Array.isArray(values))
    throw new Error("Expected byte array");
  checkBytes(values.length);
  if (values.some((v) => !Number.isInteger(v) || v < 0 || v > 255))
    throw new Error("Expected bytes in 0..255");
  return Uint8Array.from(values);
}
export function storageKey(key) {
  if (
    typeof key !== "string" ||
    !/^[-_.a-zA-Z0-9]{1,128}$/.test(key) ||
    [".", ".."].includes(key)
  )
    throw new Error("Invalid storage key");
  return key;
}
export function archiveName(name) {
  if (
    typeof name !== "string" ||
    !name ||
    /[\\:\0]/.test(name) ||
    name.startsWith("/") ||
    name.split("/").some((p) => ["", ".", ".."].includes(p))
  )
    throw new Error("Invalid archive resource name");
  return name;
}
export function normalize(path) {
  if (typeof path !== "string" || path.includes("\0"))
    throw new Error("Invalid path");
  path = path.replaceAll("\\", "/");
  let root = "";
  if (path.startsWith("//")) {
    const parts = path.slice(2).split("/").filter(Boolean);
    if (parts.length < 2) throw new Error("UNC path requires server and share");
    root = `//${parts[0]}/${parts[1]}/`;
    path = parts.slice(2).join("/");
  } else if (/^[a-z]:/i.test(path)) {
    if (path[2] !== "/") throw new Error("Drive-relative paths are ambiguous");
    root = path[0].toUpperCase() + ":/";
    path = path.slice(3);
  } else if (path.startsWith("/")) {
    root = "/";
    path = path.replace(/^\/+/, "");
  }
  const result = [];
  for (const part of path.split("/").filter(Boolean)) {
    if (part === ".") continue;
    if (part === "..") {
      if (result.length && result.at(-1) !== "..") result.pop();
      else if (!root) result.push(part);
    } else result.push(part);
  }
  return root + result.join("/") || ".";
}
const root = (p) =>
  p.startsWith("//")
    ? p.split("/").slice(0, 4).join("/") + "/"
    : p.startsWith("/")
      ? "/"
      : /^[A-Z]:\//.test(p)
        ? p.slice(0, 3)
        : "";
export const paths = {
  Normalize: normalize,
  Join: (parts) => {
    if (!Array.isArray(parts)) throw new Error("Expected path array");
    let out = "";
    for (const part of parts) {
      const n = normalize(part);
      out = root(n) || !out ? n : out + "/" + n;
    }
    return normalize(out);
  },
  Name: (p) => {
    p = normalize(p);
    return p === root(p) || p === "." ? "" : p.slice(p.lastIndexOf("/") + 1);
  },
  Extension: (p) => {
    const n = paths.Name(p),
      i = n.lastIndexOf(".");
    return i <= 0 ? "" : n.slice(i);
  },
  Parent: (p) => {
    p = normalize(p);
    const r = root(p),
      i = p.lastIndexOf("/");
    return p === r ? p : i < 0 ? "." : i < r.length ? r : p.slice(0, i);
  },
  Relative: (a, b) => {
    a = normalize(a);
    b = normalize(b);
    if (root(a) !== root(b)) throw new Error("Paths have different roots");
    const split = (p) => p.split("/").filter((v) => v && v !== "."),
      x = split(a),
      y = split(b);
    let i = 0;
    while (i < x.length && i < y.length && x[i] === y[i]) i++;
    return [...Array(x.length - i).fill(".."), ...y.slice(i)].join("/") || ".";
  },
};
export function strictText(text, maximum = limits.maxBytes) {
  if (typeof text !== "string") throw new Error("Expected string");
  if (text.length > maximum)
    throw new Error("LimitExceeded: Editor text too long");
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    if (c >= 0xd800 && c <= 0xdbff) {
      const next = text.charCodeAt(++i);
      if (!(next >= 0xdc00 && next <= 0xdfff))
        throw new Error("Invalid Unicode");
    } else if (c >= 0xdc00 && c <= 0xdfff) throw new Error("Invalid Unicode");
  }
  return text;
}
export function encodeText(text, encoding) {
  strictText(text);
  encoding = encoding.toLowerCase();
  if (encoding === "utf-8") return new TextEncoder().encode(text);
  if (encoding === "ascii") {
    if ([...text].some((c) => c.charCodeAt(0) > 127))
      throw new Error("Text is not ASCII");
    return Uint8Array.from(text, (c) => c.charCodeAt(0));
  }
  if (!["utf-16le", "utf-16be"].includes(encoding))
    throw new Error("Unsupported encoding");
  const out = new Uint8Array(text.length * 2),
    view = new DataView(out.buffer);
  for (let i = 0; i < text.length; i++)
    view.setUint16(i * 2, text.charCodeAt(i), encoding === "utf-16le");
  return out;
}
export function decodeText(data, encoding) {
  encoding = encoding.toLowerCase();
  if (!["utf-8", "utf-16le", "utf-16be", "ascii"].includes(encoding))
    throw new Error("Unsupported encoding");
  if (encoding === "ascii") {
    if (data.some((v) => v > 127)) throw new Error("Invalid ASCII");
    return (
      String.fromCharCode(...data.subarray(0, 0)) +
      Array.from(data, (v) => String.fromCharCode(v)).join("")
    );
  }
  return new TextDecoder(encoding, { fatal: true, ignoreBOM: true }).decode(
    data,
  );
}
export function jsonValue(value, depth = 0) {
  if (depth > limits.maxDepth)
    throw new Error("LimitExceeded: JSON depth exceeds limit or cycle");
  if (value === null || typeof value === "boolean" || typeof value === "string")
    return value;
  if (typeof value === "number" && Number.isFinite(value)) return value;
  if (Array.isArray(value)) return value.map((v) => jsonValue(v, depth + 1));
  if (value && Object.getPrototypeOf(value) === Object.prototype) {
    const out = Object.create(null);
    for (const [k, v] of Object.entries(value))
      out[k] = jsonValue(v, depth + 1);
    return out;
  }
  throw new Error("Unsupported JSON value");
}
export function parseJson(text) {
  checkBytes(new TextEncoder().encode(text).length);
  const value = JSON.parse(text);
  jsonValue(value);
  return value;
}
export function crc32(data) {
  let crc = 0xffffffff;
  for (const b of data) {
    crc ^= b;
    for (let i = 0; i < 8; i++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0);
  }
  return ~crc >>> 0;
}
export function binary(value, schema) {
  if (!Number.isInteger(schema) || schema < 1 || schema > 0xffffffff)
    throw new Error("Invalid schema");
  const payload = encodeText(JSON.stringify(jsonValue(value)), "utf-8");
  checkBytes(payload.length + 16);
  const out = new Uint8Array(payload.length + 16),
    v = new DataView(out.buffer);
  out.set([65, 81, 68, 49]);
  v.setUint32(4, schema, true);
  v.setUint32(8, payload.length, true);
  v.setUint32(12, crc32(payload), true);
  out.set(payload, 16);
  return out;
}
export function parseBinary(data) {
  checkBytes(data.length);
  if (data.length < 16 || data.slice(0, 4).join(",") !== "65,81,68,49")
    throw new Error("Invalid document header or version");
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength),
    schema = view.getUint32(4, true);
  if (
    !schema ||
    view.getUint32(8, true) !== data.length - 16 ||
    view.getUint32(12, true) !== crc32(data.subarray(16))
  )
    throw new Error("Invalid document length, schema or checksum");
  return { schema, value: parseJson(decodeText(data.subarray(16), "utf-8")) };
}
export class TextEditor {
  constructor(text = "") {
    this.text = strictText(text, 1024 * 1024);
    this.selection = { anchor: 0, caret: 0 };
    this.composition = null;
    this.boundaries();
  }
  boundaries() {
    this.offsets = [
      ...new Intl.Segmenter(undefined, { granularity: "grapheme" }).segment(
        this.text,
      ),
    ].map((s) => s.index);
    this.offsets.push(this.text.length);
  }
  get length() {
    return this.offsets.length - 1;
  }
  get start() {
    return Math.min(this.selection.anchor, this.selection.caret);
  }
  get end() {
    return Math.max(this.selection.anchor, this.selection.caret);
  }
  get selectedText() {
    return this.text.slice(this.offsets[this.start], this.offsets[this.end]);
  }
  select(anchor, caret) {
    if (
      ![anchor, caret].every(
        (p) => Number.isInteger(p) && p >= 0 && p <= this.length,
      )
    )
      throw new Error("Invalid grapheme selection");
    this.selection = { anchor, caret };
  }
  insert(text) {
    strictText(text, 1024 * 1024);
    const start = this.offsets[this.start],
      updated =
        this.text.slice(0, start) +
        text +
        this.text.slice(this.offsets[this.end]);
    strictText(updated, 1024 * 1024);
    this.text = updated;
    this.boundaries();
    const caret = this.offsets.findIndex((p) => p >= start + text.length);
    this.select(caret, caret);
  }
  backspace() {
    if (this.composition) return;
    if (this.start === this.end && this.selection.caret > 0)
      this.select(this.selection.caret - 1, this.selection.caret);
    this.insert("");
  }
  delete() {
    if (this.composition) return;
    if (this.start === this.end && this.selection.caret < this.length)
      this.select(this.selection.caret, this.selection.caret + 1);
    this.insert("");
  }
  convert(position, from, to) {
    from = from.toLowerCase();
    to = to.toLowerCase();
    const scalars = [0];
    for (const c of this.text) scalars.push(scalars.at(-1) + c.length);
    const table =
      from === "grapheme"
        ? this.offsets
        : from === "unicodescalar"
          ? scalars
          : from === "utf16"
            ? null
            : (() => {
                throw new Error("Unknown position unit");
              })();
    const utf16 = table ? table[position] : position;
    if (
      !Number.isInteger(utf16) ||
      utf16 < 0 ||
      utf16 > this.text.length ||
      !scalars.includes(utf16)
    )
      throw new Error("Position splits Unicode scalar");
    if (to === "utf16") return utf16;
    const result = (
      to === "grapheme"
        ? this.offsets
        : to === "unicodescalar"
          ? scalars
          : (() => {
              throw new Error("Unknown position unit");
            })()
    ).indexOf(utf16);
    if (result < 0) throw new Error("Position splits grapheme");
    return result;
  }
  move(direction, unit, extend) {
    if (![-1, 1].includes(direction)) throw new Error("Invalid direction");
    let next = this.selection.caret;
    const word = (i) =>
        /[\p{L}\p{N}_]/u.test(
          this.text.slice(this.offsets[i], this.offsets[i + 1]),
        ),
      line = (i) => /[\r\n]/.test(this.text[this.offsets[i]]);
    if (unit === "grapheme") {
      next =
        !extend && this.start !== this.end
          ? direction < 0
            ? this.start
            : this.end
          : Math.max(0, Math.min(this.length, next + direction));
    } else if (unit === "line") {
      if (direction < 0) while (next > 0 && !line(next - 1)) next--;
      else while (next < this.length && !line(next)) next++;
    } else if (unit === "word") {
      if (direction > 0) {
        while (next < this.length && word(next)) next++;
        while (next < this.length && !word(next)) next++;
      } else {
        while (next > 0 && !word(next - 1)) next--;
        while (next > 0 && word(next - 1)) next--;
      }
    } else throw new Error("Invalid navigation unit");
    this.select(extend ? this.selection.anchor : next, next);
  }
  setComposition(text, cursorScalar, unit = "UnicodeScalar") {
    cursorScalar = new TextEditor(text).convert(
      cursorScalar,
      unit,
      "UnicodeScalar",
    );
    strictText(text, 1024 * 1024);
    if (
      !Number.isInteger(cursorScalar) ||
      cursorScalar < 0 ||
      cursorScalar > [...text].length
    )
      throw new Error("Invalid composition cursor");
    this.composition = { text, cursorScalar };
  }
  commit(text) {
    this.composition = null;
    this.insert(text);
  }
  state() {
    return {
      text: this.text,
      length: this.length,
      selection: { ...this.selection },
      composition: this.composition,
    };
  }
}
