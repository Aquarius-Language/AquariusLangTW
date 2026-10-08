import {
  gzipSync,
  zlibSync,
  deflateSync,
  Gunzip,
  Unzlib,
  Inflate,
  zipSync,
} from "./vendor-fflate.mjs";
import {
  limits,
  checkBytes,
  archiveName,
  crc32,
  decodeText,
} from "./application-core.mjs";
export function compress(data, format, level = "Optimal") {
  checkBytes(data.length);
  const levels = { Optimal: 6, Fastest: 1, SmallestSize: 9, NoCompression: 0 };
  if (!(level in levels)) throw new Error("Invalid compression level");
  const codec = { gzip: gzipSync, zlib: zlibSync, deflate: deflateSync }[
    format.toLowerCase()
  ];
  if (!codec) throw new Error("Unsupported: Compression format");
  const out = codec(data, { level: levels[level] });
  checkBytes(out.length);
  return out;
}
export async function decompress(
  data,
  format,
  signal,
  maximum = limits.maxBytes,
) {
  checkBytes(data.length);
  const Type = { gzip: Gunzip, zlib: Unzlib, deflate: Inflate }[
    format.toLowerCase()
  ];
  if (!Type) throw new Error("Unsupported: Compression format");
  const chunks = [];
  let size = 0;
  const codec = new Type((chunk) => {
    size += chunk.length;
    if (size > maximum) throw new Error("LimitExceeded: Decompressed size");
    chunks.push(chunk);
  });
  for (let offset = 0; offset < data.length; offset += 4096) {
    signal?.throwIfAborted();
    codec.push(
      data.subarray(offset, offset + 4096),
      offset + 4096 >= data.length,
    );
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
  if (!data.length) codec.push(data, true);
  signal?.throwIfAborted();
  const out = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) {
    out.set(chunk, offset);
    offset += chunk.length;
  }
  const mode = format.toLowerCase(),
    view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  let invalid = false;
  if (mode === "gzip")
    invalid =
      data.length < 18 ||
      data[0] !== 31 ||
      data[1] !== 139 ||
      data[2] !== 8 ||
      (data[3] & 224) !== 0 ||
      view.getUint32(data.length - 8, true) !== crc32(out) ||
      view.getUint32(data.length - 4, true) !== out.length;
  if (mode === "zlib") {
    let a = 1,
      b = 0;
    for (const value of out) {
      a = (a + value) % 65521;
      b = (b + a) % 65521;
    }
    invalid =
      data.length < 6 ||
      (data[0] & 15) !== 8 ||
      data[0] >>> 4 > 7 ||
      ((data[0] << 8) | data[1]) % 31 !== 0 ||
      (data[1] & 32) !== 0 ||
      view.getUint32(data.length - 4, false) !== ((b << 16) | a) >>> 0;
  }
  if (invalid)
    throw new Error(
      "Invalid compressed checksum, header or length; one gzip member is supported",
    );
  return out;
}
export function archive(entries) {
  if (Object.keys(entries).length > limits.maxEntries)
    throw new Error("LimitExceeded: Archive entries");
  let total = 0;
  const values = Object.create(null);
  for (const [name, data] of Object.entries(entries)) {
    archiveName(name);
    total += data.length;
    checkBytes(total);
    values[name] = data;
  }
  const out = zipSync(values, { level: 6 });
  checkBytes(out.length);
  return out;
}
export async function readArchive(data, signal) {
  checkBytes(data.length);
  if (data.length < 22) throw new Error("Invalid ZIP header");
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  let end = data.length - 22;
  while (
    end >= Math.max(0, data.length - 65557) &&
    view.getUint32(end, true) !== 0x06054b50
  )
    end--;
  if (
    end < 0 ||
    view.getUint32(end, true) !== 0x06054b50 ||
    end + 22 + view.getUint16(end + 20, true) !== data.length
  )
    throw new Error("Invalid ZIP end record");
  const count = view.getUint16(end + 10, true),
    start = view.getUint32(end + 16, true),
    centralSize = view.getUint32(end + 12, true);
  if (
    view.getUint16(end + 4, true) !== 0 ||
    view.getUint16(end + 6, true) !== 0 ||
    view.getUint16(end + 8, true) !== count ||
    count === 65535 ||
    start === 0xffffffff ||
    centralSize === 0xffffffff ||
    start + centralSize !== end
  )
    throw new Error("Unsupported: Multipart or ZIP64 archive");
  if (count > limits.maxEntries)
    throw new Error("LimitExceeded: Archive entries");
  const entries = [];
  let position = start,
    total = 0;
  const names = new Set();
  for (let i = 0; i < count; i++) {
    if (position + 46 > end || view.getUint32(position, true) !== 0x02014b50)
      throw new Error("Invalid ZIP directory");
    const flags = view.getUint16(position + 8, true),
      method = view.getUint16(position + 10, true),
      crc = view.getUint32(position + 16, true),
      compressed = view.getUint32(position + 20, true),
      size = view.getUint32(position + 24, true),
      nameLength = view.getUint16(position + 28, true),
      extra = view.getUint16(position + 30, true),
      comment = view.getUint16(position + 32, true),
      local = view.getUint32(position + 42, true);
    if (flags & 1 || ![0, 8].includes(method))
      throw new Error("Unsupported: Encrypted ZIP or compression method");
    if (
      position + 46 + nameLength + extra + comment > end ||
      local + 30 > start ||
      view.getUint32(local, true) !== 0x04034b50
    )
      throw new Error("Invalid ZIP offsets");
    const name = archiveName(
      decodeText(
        data.subarray(position + 46, position + 46 + nameLength),
        "utf-8",
      ),
    );
    if (names.has(name)) throw new Error("Duplicate archive resource");
    names.add(name);
    total += size;
    checkBytes(total);
    const localNameLength = view.getUint16(local + 26, true),
      payload = local + 30 + localNameLength + view.getUint16(local + 28, true);
    if (
      payload + compressed > start ||
      decodeText(
        data.subarray(local + 30, local + 30 + localNameLength),
        "utf-8",
      ) !== name ||
      view.getUint16(local + 8, true) !== method
    )
      throw new Error("Invalid ZIP local entry");
    entries.push({
      name,
      crc,
      size,
      method,
      data: data.subarray(payload, payload + compressed),
    });
    position += 46 + nameLength + extra + comment;
  }
  if (position !== end) throw new Error("Invalid ZIP directory length");
  const output = Object.create(null);
  for (const entry of entries) {
    signal?.throwIfAborted();
    const bytes =
      entry.method === 0
        ? entry.data.slice()
        : await decompress(entry.data, "deflate", signal, entry.size);
    if (bytes.length !== entry.size || crc32(bytes) !== entry.crc)
      throw new Error("Invalid ZIP checksum or length");
    output[entry.name] = bytes;
  }
  return output;
}
