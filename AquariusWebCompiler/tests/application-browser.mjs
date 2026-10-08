// Real Chromium integration. Printing is intercepted at the dialog boundary; no physical page is sent.
import { chromium } from "playwright";
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
const root = path.resolve(
    path.dirname(fileURLToPath(import.meta.url)),
    "../..",
  ),
  site = path.join(root, ".web-build/application");
const types = {
  ".mjs": "text/javascript",
  ".html": "text/html",
  ".json": "application/json",
  ".wasm": "application/wasm",
};
const server = createServer(async (req, res) => {
  try {
    const location = new URL(req.url, "http://localhost").pathname,
      file = path.resolve(
        site,
        "." + location + (location.endsWith("/") ? "index.html" : ""),
      );
    if (!file.startsWith(site + path.sep)) throw new Error("Outside site");
    res.setHeader(
      "Content-Type",
      types[path.extname(file)] ?? "application/octet-stream",
    );
    res.end(await readFile(file));
  } catch (e) {
    res.statusCode = 404;
    res.end(e.message);
  }
});
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const browser = await chromium.launch({
  channel: process.env.AQUARIUS_BROWSER ?? "chrome",
  headless: true,
  args: ["--enable-unsafe-webgpu"],
});
try {
  const context = await browser.newContext({
      permissions: ["clipboard-read", "clipboard-write"],
    }),
    page = await context.newPage(),
    errors = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await page.goto(`http://127.0.0.1:${server.address().port}/?autorun=0`);
  await page.waitForFunction(() => !!window.aquarius);
  await page.evaluate(() => window.aquarius.run("increment.aqua", 1));
  const result = await page.evaluate(async () => {
    const h = window.aquarius.host,
      a = h.application,
      signal = a.signal(),
      r = a.files.resolve("persistent:/test/document"),
      source = {
        width: 2,
        height: 1,
        pixels: new Uint8Array([255, 0, 0, 255, 0, 255, 0, 128]),
      };
    const encoded = await a.images.encode(source, "Png", {}, signal);
    const processingImage = h.processing.image(2, 1, source.pixels.slice());
    const convert = h.modules.get("Window").scope.get("FromProcessingImage");
    const snapshot = a.own(await convert(processingImage.module));
    processingImage.bytes[7] = 0;
    if (snapshot.pixels[7] !== 128) throw new Error("Image snapshot ownership failed");
    await h.processing.module.scope.get("size")(2, 1);
    h.processing.screen.resize(2, 1, 4, 2);
    h.processing.screen.target.clear([1, 0, 0, 1]);
    const canvasSnapshot = a.own(await convert(h.processing.module));
    if (canvasSnapshot.width !== 4 || canvasSnapshot.height !== 2 || canvasSnapshot.pixels[0] !== 255)
      throw new Error("Existing graphics readback integration failed");
    await a.files.replace(r, encoded, true, signal);
    const decoded = await a.images.decode(
      await a.files.read(r, signal),
      0,
      signal,
    );
    if (decoded.pixels[7] !== 128) throw new Error("Worker alpha failed");
    const stream = await a.files.open(r, "Read", signal);
    const first = stream.Read(8);
    stream.Seek(0, "Begin");
    if (stream.Read(8)[0] !== first[0]) throw new Error("Seek failed");
    await stream.Close();
    await a.storage.write("preference-test", new Uint8Array([1, 2, 3]), signal);
    const other = new (await import("./application-files.mjs")).BrowserStorage(
      h.bundle.entry,
    );
    if ((await other.read("preference-test"))[1] !== 2)
      throw new Error("Persistent settings failed");
    await other.dispose();
    const temporary = a.files.temporaryResource(true),
      child = a.files.resolve(temporary.id + "/child");
    await a.files.replace(child, new Uint8Array([7]), true, signal);
    if ((await a.files.enumerate(temporary, signal)).length !== 1)
      throw new Error("Temporary directory failed");
    await a.files.remove(temporary, true, signal);
    try {
      await a.files.read(child, signal);
      throw new Error("Temporary ownership remained valid");
    } catch (error) {
      if (error.message === "Temporary ownership remained valid") throw error;
    }
    const canvas = document.createElement("canvas");
    canvas.style.width = "200px";
    canvas.style.height = "100px";
    canvas.width = 200;
    canvas.height = 100;
    canvas.tabIndex = 0;
    document.getElementById("surfaces").append(canvas);
    canvas.focus();
    window.testWindow = a.window(canvas);
    window.testCanvas = canvas;
    a.services.updateAccessibility([
      { id: "status", role: "status", label: "Saved", value: "complete" },
    ]);
    if (
      document
        .querySelector('[data-aquarius-id="status"]')
        .getAttribute("aria-valuetext") !== "complete"
    )
      throw new Error("Accessibility failed");
    const transfer = new DataTransfer();
    transfer.items.add(new File([encoded], "drop.png", { type: "image/png" }));
    canvas.dispatchEvent(
      new DragEvent("drop", { bubbles: true, dataTransfer: transfer }),
    );
    const events = await window.testWindow.scope.get("Poll")();
    window.droppedResource = events
      .find((e) => e.scope.get("type") === "fileDrop")
      .scope.get("files")[0];
    if ((await a.files.read(a.own(window.droppedResource), signal))[0] !== 137)
      throw new Error("Drop resource failed");
    await a.clipboard.write({ text: "繁😀", image: source }, signal);
    const content = await a.clipboard.read(signal);
    if (content.text !== "繁😀" || content.image.pixels[7] !== 128)
      throw new Error("Clipboard exchange failed");
    const token = await h.modules
      .get("Tasks")
      .scope.get("CreateCancellation")();
    await h.modules.get("Tasks").scope.get("UseCancellation")(token);
    await token.scope.get("Cancel")();
    if (!(await token.scope.get("IsCancelled")()))
      throw new Error("Cancellation failed");
    await h.modules.get("Tasks").scope.get("UseCancellation")(null);
    if (await token.scope.get("Dispose")() !== null) throw new Error("Cancellation disposal return parity failed");
    await a.files.remove(r, false, signal);
    await a.storage.remove("preference-test", signal);
    return {
      formats: (await a.images.inspect(encoded, signal)).format,
      clipboard: await a.clipboard.formats(signal),
    };
  });
  assert.equal(result.formats, "Png");
  assert.ok(result.clipboard.includes("image/png"));
  await page.keyboard.press("a");
  const events = await page.evaluate(async () => {
    const records = await window.testWindow.scope.get("Poll")();
    return records.map((e) => ({
      type: e.scope.get("type"),
      physical: e.scope.get("physicalKey"),
    }));
  });
  assert.ok(
    events.some((e) => e.type === "keyPressed" && e.physical === "KeyA"),
  );
  const selectedChooser = page.waitForEvent("filechooser");
  await page.evaluate(() => {
    const a = window.aquarius.host.application;
    window.pickTask = a.services
      .inputFiles({ filters: [{ extensions: ["png"] }] })
      .then((files) => a.files.read(files[0]));
  });
  await (
    await selectedChooser
  ).setFiles({
    name: "selected.txt",
    mimeType: "text/plain",
    buffer: Buffer.from("selected"),
  });
  assert.equal(
    await page.evaluate(async () =>
      new TextDecoder().decode(await window.pickTask),
    ),
    "selected",
  );
  const png = await page.evaluate(async () =>
    Array.from(
      await window.aquarius.host.application.images.encode(
        { width: 1, height: 1, pixels: new Uint8Array([1, 2, 3, 255]) },
        "Png",
        {},
      ),
    ),
  );
  const imageChooser = page.waitForEvent("filechooser");
  await page.evaluate(() => {
    window.acquisition =
      window.aquarius.host.application.services.acquireImage();
  });
  await (
    await imageChooser
  ).setFiles({
    name: "capture.png",
    mimeType: "image/png",
    buffer: Buffer.from(png),
  });
  assert.equal(
    await page.evaluate(async () => (await window.acquisition).pixels[2]),
    3,
  );
  await page.evaluate(() => {
    window.printed = false;
    const original = document.createElement.bind(document);
    document.createElement = function (name, ...args) {
      const element = original(name, ...args);
      if (name === "iframe")
        element.addEventListener("load", () => {
          element.contentWindow.print = () => (window.printed = true);
        });
      return element;
    };
  });
  await page.evaluate(() =>
    window.aquarius.host.application.services.printImage({
      width: 1,
      height: 1,
      pixels: new Uint8Array([1, 2, 3, 255]),
    }),
  );
  assert.equal(await page.evaluate(() => window.printed), true);
  await page.evaluate(() => window.aquarius.stop());
  assert.deepEqual(errors, []);
  console.log(
    "PASS browser files, codecs, clipboard, drops, input, cancellation, storage, accessibility, acquisition and print preparation",
  );
} finally {
  await browser.close();
  server.close();
}
