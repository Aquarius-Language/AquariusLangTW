import fs from "node:fs";
import {
  parseBinary,
  paths,
  TextEditor,
} from "../../AquariusCore/application/portable.mjs";
import {
  readArchive,
  decompress,
} from "../browser/application-serialization.mjs";
import { initializeCodec, decodeImage } from "../browser/codec-engine.mjs";
const input = JSON.parse(fs.readFileSync(process.argv[2], "utf-8"));
await initializeCodec(
  fs.readFileSync(new URL("../browser/vendor-magick.wasm", import.meta.url)),
);
const document = parseBinary(Uint8Array.from(input.document)),
  archive = await readArchive(Uint8Array.from(input.archive));
const image = decodeImage(Uint8Array.from(input.image), 0),
  editor = new TextEditor("e\u0301👨‍👩‍👧‍👦繁");
editor.select(3, 3);
editor.backspace();
editor.setComposition("😀字", 2, "Utf16");
const compressed = {};
for (const [format, data] of Object.entries(input.compressed))
  compressed[format] = Array.from(
    await decompress(Uint8Array.from(data), format),
  );
console.log(
  JSON.stringify({
    document,
    archive: Object.fromEntries(
      Object.entries(archive).map(([k, v]) => [k, Array.from(v)]),
    ),
    pixels: Array.from(image.pixels),
    text: editor.state(),
    path: paths.Relative("/a", "/b/c"),
    compressed,
  }),
);
