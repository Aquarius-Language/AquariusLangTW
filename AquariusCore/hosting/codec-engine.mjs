import {
  ImageMagick,
  MagickImageCollection,
  MagickReadSettings,
  MagickFormat,
  ResourceLimits,
  initializeImageMagick,
  ConfigurationFiles,
  Density,
  DensityUnit,
  ColorType,
  CompressionMethod,
} from "./vendor-magick.mjs";
import { limits, checkBytes, checkPixels } from "./application-core.mjs";
const formats = {
  Png: MagickFormat.Png,
  Jpeg: MagickFormat.Jpeg,
  Bmp: MagickFormat.Bmp,
  Gif: MagickFormat.Gif,
  Tiff: MagickFormat.Tiff,
};
export function identify(data) {
  const signature = Array.from(data.subarray(0, 8)).join(",");
  if (signature === "137,80,78,71,13,10,26,10") return "Png";
  if (data[0] === 255 && data[1] === 216 && data[2] === 255) return "Jpeg";
  if (data[0] === 66 && data[1] === 77) return "Bmp";
  if (
    ["GIF87a", "GIF89a"].includes(String.fromCharCode(...data.subarray(0, 6)))
  )
    return "Gif";
  if (
    ["73,73,42,0", "77,77,0,42"].includes(
      Array.from(data.subarray(0, 4)).join(","),
    )
  )
    return "Tiff";
  throw new Error("Unsupported: Unknown image signature");
}
let initialized;
export async function initializeCodec(
  wasm = new URL("./vendor-magick.wasm", import.meta.url),
) {
  initialized ??= (async () => {
    const config = ConfigurationFiles.default;
    config.policy.data = `<policymap><policy domain="delegate" rights="none" pattern="*"/><policy domain="path" rights="none" pattern="*"/><policy domain="coder" rights="none" pattern="*"/><policy domain="coder" rights="read|write" pattern="{PNG,JPEG,BMP,GIF,TIFF,RGBA}"/></policymap>`;
    await initializeImageMagick(wasm, config);
    ResourceLimits.width = 8192n;
    ResourceLimits.height = 8192n;
    ResourceLimits.area = 16777216n;
    ResourceLimits.listLength = 256n;
    ResourceLimits.memory = 134217728n;
    ResourceLimits.maxMemoryRequest = 67108864n;
    ResourceLimits.disk = 0n;
    ResourceLimits.time = 30n;
    ResourceLimits.maxProfileSize = 1048576n;
  })();
  await initialized;
}
function metadata(image) {
  const d = image.density,
    factor = d.units === DensityUnit.PixelsPerCentimeter ? 2.54 : 1,
    palette = [];
  for (let i = 0; i < image.colormapSize && i < 256; i++) {
    const c = image.getColormapColor(i);
    palette.push(((c.r << 24) | (c.g << 16) | (c.b << 8) | c.a) >>> 0);
  }
  return {
    orientation: image.orientation || 1,
    dpiX: d.units && d.x > 0 ? d.x * factor : null,
    dpiY: d.units && d.y > 0 ? d.y * factor : null,
    sourcePixelFormat:
      Object.keys(ColorType).find(
        (key) => ColorType[key] === image.colorType,
      ) ?? "unknown",
    palette: palette.length ? palette : null,
  };
}
export function inspectImage(data) {
  checkBytes(data.length);
  const format = identify(data),
    collection = MagickImageCollection.create();
  try {
    collection.ping(data, new MagickReadSettings({ format: formats[format] }));
    if (!collection.length || collection.length > limits.maxFrames)
      throw new Error("LimitExceeded: Frame count");
    let pixels = 0;
    for (const image of collection) {
      checkPixels(image.width, image.height);
      pixels += image.width * image.height;
    }
    if (pixels > limits.maxPixels)
      throw new Error("LimitExceeded: Frame pixels");
    const image = collection[0];
    return {
      format,
      width: image.width,
      height: image.height,
      frames: collection.length,
      metadata: metadata(image),
    };
  } finally {
    collection.dispose();
  }
}
export function decodeImage(data, frame) {
  const info = inspectImage(data);
  if (!Number.isInteger(frame) || frame < 0 || frame >= info.frames)
    throw new Error("Select valid frame/page");
  return ImageMagick.readCollection(
    data,
    new MagickReadSettings({ format: formats[info.format] }),
    (collection) => {
      if (info.format === "Gif") collection.coalesce();
      const image = collection[frame];
      checkPixels(image.width, image.height);
      return {
        width: image.width,
        height: image.height,
        alpha: "Straight",
        metadata: metadata(image),
        pixels: image
          .getPixels((p) =>
            p.toByteArray(0, 0, image.width, image.height, "RGBA"),
          )
          .slice(),
      };
    },
  );
}
function prepare(image, format, options) {
  checkPixels(image.width, image.height);
  if (image.pixels.length !== image.width * image.height * 4)
    throw new Error("RGBA size mismatch");
  const quality = options.quality ?? 90,
    compression = options.compression ?? 6;
  if (
    !Number.isInteger(quality) ||
    quality < 1 ||
    quality > 100 ||
    !Number.isInteger(compression) ||
    compression < 0 ||
    compression > 9
  )
    throw new Error("Invalid encoding options");
  const pixels = image.pixels.slice();
  if (image.alpha === "Premultiplied")
    for (let i = 0; i < pixels.length; i += 4)
      for (let c = 0; c < 3; c++)
        pixels[i + c] = pixels[i + 3]
          ? Math.min(
              255,
              Math.floor(
                (pixels[i + c] * 255 + pixels[i + 3] / 2) / pixels[i + 3],
              ),
            )
          : 0;
  let transparent = false,
    fractional = false;
  for (let i = 3; i < pixels.length; i += 4) {
    transparent ||= pixels[i] < 255;
    fractional ||= pixels[i] > 0 && pixels[i] < 255;
  }
  if (
    transparent &&
    (["Jpeg", "Bmp"].includes(format) || (format === "Gif" && fractional))
  ) {
    if (
      !Number.isInteger(options.background) ||
      options.background < 0 ||
      options.background > 0xffffffff
    )
      throw new Error("Conversion requires explicit opaque RGB background");
    for (let i = 0; i < pixels.length; i += 4) {
      const a = pixels[i + 3];
      for (let c = 0; c < 3; c++)
        pixels[i + c] = Math.floor(
          (pixels[i + c] * a +
            ((options.background >>> (16 - c * 8)) & 255) * (255 - a) +
            127) /
            255,
        );
      pixels[i + 3] = 255;
    }
  }
  if (
    !options.allowMetadataLoss &&
    ["Gif", "Bmp"].includes(format) &&
    (image.metadata?.orientation ?? 1) !== 1
  )
    throw new Error(
      "Target cannot preserve orientation; allow metadata loss explicitly",
    );
  const metadata = image.metadata ?? {}, orientation = metadata.orientation ?? 1;
  if (!Number.isInteger(orientation) || orientation < 1 || orientation > 8 ||
      [metadata.dpiX, metadata.dpiY].some(v => v != null && (!Number.isFinite(v) || v <= 0)))
    throw new Error("Invalid orientation or resolution");
  if (!options.allowMetadataLoss && format === "Gif" && (metadata.dpiX != null || metadata.dpiY != null))
    throw new Error("GIF cannot preserve physical resolution; allow metadata loss explicitly");
  return pixels;
}
export function encodeImage(image, format, options = {}) {
  if (!formats[format]) throw new Error("Unsupported codec");
  const pixels = prepare(image, format, options),
    settings = new MagickReadSettings({
      format: MagickFormat.Rgba,
      width: image.width,
      height: image.height,
      depth: 8,
    });
  return ImageMagick.read(pixels, settings, (native) => {
    native.depth = 8;
    native.quality = options.quality ?? 90;
    native.settings.setDefine(
      MagickFormat.Png,
      "compression-level",
      options.compression ?? 6,
    );
    if (format === "Tiff") {
      native.settings.compression = (options.compression ?? 6) === 0
        ? CompressionMethod.NoCompression : CompressionMethod.Zip;
      // The TIFF coder derives its deflate level from quality / 10.
      native.quality = (options.compression ?? 6) * 10;
    }
    const m = image.metadata ?? {};
    if (m.dpiX || m.dpiY)
      native.density = new Density(
        m.dpiX ?? m.dpiY,
        m.dpiY ?? m.dpiX,
        DensityUnit.PixelsPerInch,
      );
    native.orientation = m.orientation ?? 1;
    if (["Png", "Jpeg", "Tiff"].includes(format)) {
      const exif = new Uint8Array(32),
        v = new DataView(exif.buffer);
      exif.set([69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0, 1, 0]);
      v.setUint16(16, 274, true);
      v.setUint16(18, 3, true);
      v.setUint32(20, 1, true);
      v.setUint16(24, m.orientation ?? 1, true);
      native.setProfile("exif", exif);
    }
    return native.write(formats[format], (data) => {
      checkBytes(data.length);
      const out = data.slice();
      if (identify(out) !== format)
        throw new Error("Encoder returned wrong format");
      return out;
    });
  });
}
