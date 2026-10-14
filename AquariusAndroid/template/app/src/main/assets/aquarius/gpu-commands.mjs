// Little-endian AQG1 records. Resource creation stays outside the frame stream.
// Draw: 64-byte header, 1120 standard-uniform bytes, 1024 user-uniform bytes,
// then tightly packed 48-byte vertices. Clear: 32 bytes including RGBA floats.
export const MAX_BATCH_BYTES = 64 * 1024 * 1024;
export class GpuCommands {
  constructor() { this.bytes = new Uint8Array(65536); this.reset(); }
  reset() { this.length = 8; this.count = 0; this.last = null; }
  reserve(size) {
    if (size > MAX_BATCH_BYTES) throw new Error('GPU batch exceeds 64 MiB');
    if (size <= this.bytes.length) return;
    let capacity = this.bytes.length;
    while (capacity < size) capacity *= 2;
    const next = new Uint8Array(capacity); next.set(this.bytes.subarray(0, this.length)); this.bytes = next;
  }
  id(view, offset, id) { view.setUint32(offset, id >>> 0, true); view.setUint32(offset + 4, Math.floor(id / 4294967296), true); }
  clear(target, color) {
    this.reserve(this.length + 32); const offset = this.length, view = new DataView(this.bytes.buffer);
    view.setUint32(offset, 1, true); view.setUint32(offset + 4, 32, true); this.id(view, offset + 8, target);
    for (let i = 0; i < 4; i++) view.setFloat32(offset + 16 + i * 4, color[i], true);
    this.length += 32; this.count++; this.last = null;
  }
  equal(offset, value) {
    for (let i = 0; i < value.length; i++) if (this.bytes[offset + i] !== value[i]) return false;
    return true;
  }
  header(offset, length, operation, target, shader, texture, depth, blend, mode, clip, vertexBytes) {
    const view = new DataView(this.bytes.buffer);
    view.setUint32(offset, operation, true); view.setUint32(offset + 4, length, true);
    this.id(view, offset + 8, target); this.id(view, offset + 16, shader); this.id(view, offset + 24, texture);
    view.setUint32(offset + 32, depth ? 1 : 0, true); view.setInt32(offset + 36, blend, true); view.setUint32(offset + 40, mode, true);
    for (let i = 0; i < 4; i++) view.setUint32(offset + 44 + i * 4, clip[i], true);
    view.setUint32(offset + 60, vertexBytes, true);
  }
  draw(target, shader, texture, depth, blend, mode, clip, vertices, uniforms, user) {
    if (uniforms.length !== 1120 || user.length !== 1024 || !vertices.length || vertices.length % 48) throw new Error('Invalid draw transport');
    const key = [target, shader, texture, depth ? 1 : 0, blend, mode, ...clip].join(':');
    const last = this.last;
    // Only merge adjacent complete primitives with identical render state.
    const primitiveBytes = mode === 4 ? 144 : mode === 1 ? 96 : 48;
    const complete = vertices.length % primitiveBytes === 0;
    const merge = last?.key === key && last.complete && complete && [0, 1, 4].includes(mode) &&
      this.equal(last.offset + 64, uniforms) && this.equal(last.offset + 1184, user);
    this.reserve(this.length + vertices.length + (merge ? 0 : 2208));
    const view = new DataView(this.bytes.buffer);
    if (merge) {
      this.bytes.set(vertices, this.length); this.length += vertices.length;
      view.setUint32(last.offset + 4, this.length - last.offset, true);
      view.setUint32(last.offset + 60, view.getUint32(last.offset + 60, true) + vertices.length, true);
      return;
    }
    const offset = this.length;
    this.header(offset, 2208 + vertices.length, 2, target, shader, texture, depth, blend, mode, clip, vertices.length);
    this.bytes.set(uniforms, offset + 64); this.bytes.set(user, offset + 1184); this.bytes.set(vertices, offset + 2208);
    this.length += 2208 + vertices.length; this.count++; this.last = {offset, key, complete};
  }
  mesh(target, shader, texture, depth, blend, clip, mesh, uniforms, user) {
    if (uniforms.length !== 1120 || user.length !== 1024) throw new Error('Invalid mesh uniforms');
    this.reserve(this.length + 2216); const offset = this.length;
    this.header(offset, 2216, 3, target, shader, texture, depth, blend, 4, clip, 0);
    this.bytes.set(uniforms, offset + 64); this.bytes.set(user, offset + 1184);
    this.id(new DataView(this.bytes.buffer), offset + 2208, mesh);
    this.length += 2216; this.count++; this.last = null;
  }
  data() {
    const view = new DataView(this.bytes.buffer); view.setUint32(0, 0x31475141, true); view.setUint32(4, this.count, true);
    return this.bytes.subarray(0, this.length);
  }
}
