import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import os from 'node:os';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
import {fileURLToPath, pathToFileURL} from 'node:url';
import {performance} from 'node:perf_hooks';
import {CapabilityHost} from '../AquariusCore/hosting/capability-host.mjs';
import {nativeCall} from '../AquariusCore/hosting/values.mjs';

// Uses the production Processing and Android GPU adapter. The base64 measurements
// remain as a reference for the former GPU transport and other host services.
// The native boundary is a counting stub: these are host-JS measurements, not
// Android FPS, JNI, Vulkan or GPU measurements. No device is needed or modified.
const root = fileURLToPath(new URL('../', import.meta.url));
const androidAssets = path.join(root, 'AquariusAndroid/template/app/src/main/assets/aquarius');
const coreAssets = path.join(root, 'AquariusCore/hosting');
const bootstrap = fs.readFileSync(path.join(androidAssets, 'bootstrap.js'), 'utf8');
const gpuSource = fs.readFileSync(path.join(androidAssets, 'android-gpu.mjs'), 'utf8');
const bootstrapContext = vm.createContext({WebAssembly: {}, native: {}});
vm.runInContext(bootstrap, bootstrapContext);
globalThis.encode64 = bootstrapContext.encode64;
globalThis.decode64 = bootstrapContext.decode64;

let counters;
let id = 0;
const reset = () => counters = {commands: {}, binaryBytes: 0, base64Characters: 0, vertexBytes: 0, drawRecords: 0};
reset();
globalThis.native = {
  gpu(operation, json, payload) {
    const args = JSON.parse(json);
    counters.commands[operation] = (counters.commands[operation] ?? 0) + 1;
    assert.ok(payload instanceof Uint8Array);
    counters.binaryBytes += payload.length;
    if (operation === 'batch') {
      const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength); let offset = 8;
      for (let i = 0; i < view.getUint32(4,true); i++) {
        const operation=view.getUint32(offset,true);
        if (operation===2||operation===3) { counters.drawRecords++; counters.vertexBytes += view.getUint32(offset+60,true); }
        offset += view.getUint32(offset+4,true);
      }
      assert.equal(offset,payload.length);
    }
    return JSON.stringify(['device', 'texture', 'target', 'shader', 'buffer','mesh'].includes(operation) ? ++id : null);
  }
};
const resolvedGpu = gpuSource.replace(/from '\.\/([^']+)'/g, (_, name) => `from '${pathToFileURL(path.join(name==='gpu-commands.mjs'?androidAssets:coreAssets, name)).href}'`);
const {AndroidWgpuBackend} = await import('data:text/javascript;base64,' + Buffer.from(resolvedGpu).toString('base64'));
const abi = {header: '', vertex: '', fragment: '', uniformBytes: 1120};
class DiagnosticHost extends CapabilityHost {
  installProcessingInput(p) { p.textarea = {remove() {}, style: {}}; }
  geometryModule() { return WebAssembly.compile(fs.readFileSync(path.join(root, 'AquariusCore/graphics/geometry.wasm'))); }
  surface(width, height) {
    const size = {width, height, pixelWidth: width, pixelHeight: height};
    return {size, canvas: {width, height, style: {}}, requestSize() {}, readSize: () => size, apply() {}, dispose() {}};
  }
}
const host = new DiagnosticHost({version: 2, catalog: [], graphics: abi, physics: {maxBodies: 4096}}, {
  adapters: {platform: 'android', graphics: () => new AndroidWgpuBackend(abi), physics: () => ({name: 'stub'}),
    register: h => { h.application = {objects: new Map()}; }}
});
const p = host.processing;
const invoke = (name, ...args) => p.module.scope.get(name)[nativeCall](...args);
await invoke('size', 412, 915);
const median = values => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];
function measure(fn) {
  for (let i = 0; i < 3; i++) fn();
  const times = [];
  for (let i = 0; i < 9; i++) { reset(); const start = performance.now(); fn(); times.push(performance.now() - start); }
  return {medianMs: median(times), minMs: Math.min(...times), maxMs: Math.max(...times)};
}
const transport = [];
for (const byteLength of [2432, 112736, 147296, 1228800]) {
  const bytes = Uint8Array.from({length: byteLength}, (_, i) => i % 251);
  const encoded = encode64(bytes);
  assert.equal(encoded, Buffer.from(bytes).toString('base64'));
  assert.deepEqual(Array.from(decode64(encoded)), Array.from(bytes));
  transport.push({byteLength, base64Characters: encoded.length,
    productionEncode: measure(() => encode64(bytes)), productionDecode: measure(() => decode64(encoded)),
    nodeNativeEncodeReference: measure(() => Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength).toString('base64'))});
}
const scenes = [];
for (const [name, count, stroke, draw] of [
  ['100 filled rectangles', 100, false, () => invoke('rect', 10, 10, 100, 40)],
  ['100 outlined rectangles', 100, true, () => invoke('rect', 10, 10, 100, 40)],
  ['20 outlined rounded rectangles', 20, true, () => invoke('rect', 10, 10, 100, 40, 10)],
  ['20 outlined ellipses', 20, true, () => invoke('ellipse', 100, 100, 50, 50)],
  ['12 filled spheres, detail 28x18', 12, false, () => { invoke('sphereDetail', 28, 18); invoke('sphere', 49); }]
]) {
  if (stroke) invoke('stroke', 0); else invoke('noStroke');
  const frame = () => { invoke('background', 24); for (let i = 0; i < count; i++) draw(); p.device.flush(); };
  const timing = measure(frame);
  const frameCounters = structuredClone(counters);
  scenes.push({name, ...timing, ...frameCounters,
    nativeOperationsDerivedFromSource: {renderPassesBeforePresent: 1, submissionsBeforePresent: frameCounters.commands.batch}});
}
// Complete compatible primitives are merged without crossing clear/state boundaries.
for (const scene of scenes) { assert.equal(scene.commands.batch,1); assert.equal(scene.base64Characters,0); }
p.dispose();
host.graphics.dispose();
const sources = ['AquariusAndroid/template/app/src/main/assets/aquarius/bootstrap.js',
  'AquariusAndroid/template/app/src/main/assets/aquarius/android-gpu.mjs', 'AquariusCore/hosting/processing.mjs',
  'AquariusAndroid/template/app/src/main/cpp/gpu.cpp'];
const result = {measuredAt: new Date().toISOString(), environment: {platform: process.platform, arch: process.arch,
  node: process.version, cpu: os.cpus()[0].model}, method: {warmups: 3, samples: 9, statistic: 'median',
  includes: 'Production Processing geometry, binary batch packing, JSON resource descriptors and counting stub',
  excludes: 'Aquarius/Wasm callbacks, Javet/Java/JNI, wgpu, Vulkan, display and Android scheduling'},
  sourceSha256: Object.fromEntries(sources.map(name => [name, crypto.createHash('sha256').update(fs.readFileSync(path.join(root, name))).digest('hex')])),
  transport, scenes};
const output = path.resolve(process.argv[2] ?? path.join(root, '.android-build/graphics-diagnostics/host-benchmark.json'));
fs.mkdirSync(path.dirname(output), {recursive: true});
fs.writeFileSync(output, JSON.stringify(result, null, 2) + '\n');
console.table(scenes.map(s => ({scene: s.name, ms: s.medianMs.toFixed(3), draws: s.drawRecords,
  KiB: (s.binaryBytes / 1024).toFixed(1), submissionsBeforePresent: s.nativeOperationsDerivedFromSource.submissionsBeforePresent})));
console.log(`Saved ${output}`);
