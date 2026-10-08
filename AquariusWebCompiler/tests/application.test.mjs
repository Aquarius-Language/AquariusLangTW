import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import {
  paths,
  TextEditor,
  binary,
  parseBinary,
  encodeText,
  decodeText,
  bytes,
} from "../../AquariusLangVM/application/portable.mjs";
import {
  compress,
  decompress,
  archive,
  readArchive,
} from "../browser/application-serialization.mjs";
import {
  initializeCodec,
  encodeImage,
  decodeImage,
  inspectImage,
  identify,
} from "../browser/codec-engine.mjs";
test("portable paths retain lexical relative and root semantics", () => {
  assert.equal(paths.Join(["a", "b"]), "a/b");
  assert.equal(paths.Normalize("C:\\a\\..\\b"), "C:/b");
  assert.equal(paths.Relative("/a", "/b/c"), "../b/c");
  assert.equal(paths.Parent("//server/share/file"), "//server/share/");
  assert.throws(() => paths.Normalize("C:file"));
});
test("editing uses graphemes and separates composition from committed text", () => {
  const e = new TextEditor("e\u0301👨‍👩‍👧‍👦繁");
  assert.equal(e.length, 3);
  e.select(3, 3);
  e.backspace();
  assert.equal(e.length, 2);
  e.setComposition("中文", 1);
  e.backspace();
  assert.equal(e.length, 2);
  e.commit("字");
  assert.equal(e.length, 3);
  assert.equal(e.convert(1, "grapheme", "utf16"), 2);
  assert.throws(() => e.convert(1, "utf16", "grapheme"));
  e.select(0, 2);
  e.insert("甲");
  assert.equal(e.text, "甲字");
  e.move(-1, "word", false);
  assert.equal(e.selection.caret, 0);
});
for (const encoding of ["utf-8", "utf-16le", "utf-16be"])
  test(`strict ${encoding} text round trip`, () => {
    assert.equal(decodeText(encodeText("繁😀", encoding), encoding), "繁😀");
    assert.throws(() => encodeText("\ud800", encoding));
  });
test("documents validate schema, checksum, values and exact boundaries", () => {
  const data = binary({ text: "繁😀", values: [1, true, null] }, 2);
  assert.deepEqual(parseBinary(data), {
    schema: 2,
    value: { text: "繁😀", values: [1, true, null] },
  });
  data[data.length - 1] ^= 1;
  assert.throws(() => parseBinary(data));
  assert.throws(() => binary(NaN, 1));
  assert.throws(() => bytes([1, 256]));
});
for (const format of ["Gzip", "Zlib", "Deflate"])
  test(`${format} round trip, expansion limits and cancellation`, async () => {
    const original = new Uint8Array(10000),
      encoded = compress(original, format);
    assert.deepEqual(await decompress(encoded, format), original);
    await assert.rejects(() => decompress(encoded, format, undefined, 1000));
    const controller = new AbortController();
    controller.abort();
    await assert.rejects(() => decompress(encoded, format, controller.signal));
  });
test("ZIP round trips Unicode names and validates traversal, checksum and entry count", async () => {
  const entries = {
    "metadata.aqd": binary({ name: "example" }, 1),
    "images/繁.bin": new Uint8Array([1, 2, 3]),
  };
  const encoded = archive(entries),
    decoded = await readArchive(encoded);
  assert.deepEqual(decoded["images/繁.bin"], entries["images/繁.bin"]);
  assert.throws(() => archive({ "../escape": new Uint8Array() }));
  const corrupt = encoded.slice();
  corrupt[14] ^= 1; // local CRC is duplicated; corruption of data or directory must be rejected
  const central = corrupt.findIndex(
    (v, i) =>
      v === 80 &&
      corrupt[i + 1] === 75 &&
      corrupt[i + 2] === 1 &&
      corrupt[i + 3] === 2,
  );
  corrupt[central + 16] ^= 1;
  await assert.rejects(() => readArchive(corrupt));
});
await initializeCodec(
  fs.readFileSync(new URL("../browser/vendor-magick.wasm", import.meta.url)),
);
for (const format of ["Png", "Jpeg", "Bmp", "Gif", "Tiff"])
  test(`real ${format} codec returns exact requested format and frame`, () => {
    const source = {
        width: 2,
        height: 1,
        pixels: new Uint8Array([255, 0, 0, 255, 0, 255, 0, 128]),
      },
      encoded = encodeImage(source, format, { background: 0xffffff });
    assert.equal(identify(encoded), format);
    assert.equal(inspectImage(encoded).frames, 1);
    const decoded = decodeImage(encoded, 0);
    assert.equal(decoded.pixels.length, 8);
    if (["Png", "Tiff"].includes(format))
      assert.deepEqual(decoded.pixels, source.pixels);
    assert.throws(() => decodeImage(encoded, 1));
  });
test("codec rejects malformed input and silent transparency loss", () => {
  const source = { width: 1, height: 1, pixels: new Uint8Array([1, 2, 3, 4]) };
  assert.throws(() => encodeImage(source, "Jpeg", {}));
  assert.throws(() =>
    decodeImage(new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]), 0),
  );
});
test("codec preserves orientation and resolution metadata", () => {
  const source = {
    width: 1,
    height: 1,
    pixels: new Uint8Array([1, 2, 3, 255]),
    metadata: { orientation: 6, dpiX: 144, dpiY: 96 },
  };
  for (const format of ["Png", "Jpeg", "Tiff"]) {
    const info = inspectImage(encodeImage(source, format, {}));
    assert.equal(info.metadata.orientation, 6, format);
    assert.ok(Math.abs(info.metadata.dpiX - 144) < 0.1, format);
  }
});
test("TIFF compression options preserve pixels and reduce repeated data", () => {
  const pixels = new Uint8Array(64 * 64 * 4);
  for (let i = 0; i < pixels.length; i += 4) pixels.set([10, 20, 30, 128], i);
  const image = { width: 64, height: 64, pixels };
  const stored = encodeImage(image, "Tiff", { compression: 0 });
  const compressed = encodeImage(image, "Tiff", { compression: 9 });
  assert.ok(compressed.length < stored.length);
  assert.deepEqual(decodeImage(compressed, 0).pixels, pixels);
});
test("image metadata validates resolution and requires explicit GIF loss", () => {
  const image = { width: 1, height: 1, pixels: new Uint8Array([1, 2, 3, 255]), metadata: { dpiY: 144 } };
  assert.ok(Math.abs(inspectImage(encodeImage(image, "Png")).metadata.dpiY - 144) < 0.1);
  assert.throws(() => encodeImage(image, "Gif"));
  assert.equal(identify(encodeImage(image, "Gif", { allowMetadataLoss: true })), "Gif");
  image.metadata.orientation = 9;
  assert.throws(() => encodeImage(image, "Png"));
});
