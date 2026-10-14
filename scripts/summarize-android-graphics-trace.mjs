import fs from 'node:fs';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';

// Read-only analysis of a Perfetto trace. QueuePresent intervals describe app
// presentation cadence, not GPU execution time or input-to-display latency.
const root = fileURLToPath(new URL('../', import.meta.url));
const trace = process.argv[2];
if (!trace) throw new Error('Usage: node scripts/summarize-android-graphics-trace.mjs TRACE [TRACE_PROCESSOR] [OUTPUT]');
const processor = process.argv[3] ?? path.join(root, '.android-build/graphics-diagnostics/trace_processor_shell.exe');
function csv(text) {
  const rows = [];
  let row = [], value = '', quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"') { if (quoted && text[i + 1] === '"') { value += '"'; i++; } else quoted = !quoted; }
    else if (c === ',' && !quoted) { row.push(value); value = ''; }
    else if (c === '\n' && !quoted) { row.push(value.replace(/\r$/, '')); if (row.some(v => v.length)) rows.push(row); row = []; value = ''; }
    else value += c;
  }
  if (value || row.length) { row.push(value); rows.push(row); }
  const header = rows.shift() ?? [];
  return rows.map(values => Object.fromEntries(header.map((name, i) => [name, values[i]])));
}
const query = sql => csv(execFileSync(processor, [path.resolve(trace), '-Q', sql], {encoding: 'utf8', maxBuffer: 16 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe']}));
const bounds = query('SELECT start_ts,end_ts FROM trace_bounds')[0];
const durationMs = (Number(bounds.end_ts) - Number(bounds.start_ts)) / 1e6;
const presents = query(`SELECT p.name AS package,p.pid,s.ts,s.dur
  FROM slice s JOIN thread_track tt ON tt.id=s.track_id JOIN thread t USING(utid) JOIN process p USING(upid)
  WHERE p.name LIKE 'org.aquariuslang.%' AND t.name='Aquarius-Wasm' AND s.name='QueuePresentKHR' AND s.dur>=0 ORDER BY s.ts`);
const operations = query(`SELECT p.name AS package,s.name AS operation,COUNT(*) AS count,SUM(s.dur)/1e6 AS totalMs,AVG(s.dur)/1e6 AS meanMs
  FROM slice s JOIN thread_track tt ON tt.id=s.track_id JOIN thread t USING(utid) JOIN process p USING(upid)
  WHERE p.name LIKE 'org.aquariuslang.%' AND t.name='Aquarius-Wasm' AND s.dur>=0
  AND s.name IN ('QueueSubmit','QueuePresentKHR','CreateSwapchainKHR','dequeueBuffer','GetPhysicalDeviceSurfaceFormatsKHR')
  GROUP BY p.name,s.name ORDER BY p.name,s.name`);
const cpu = query(`SELECT p.name AS package,t.name AS thread,t.tid,SUM(s.dur)/1e6 AS cpuMs
  FROM sched s JOIN thread t USING(utid) JOIN process p USING(upid)
  WHERE p.name LIKE 'org.aquariuslang.%' AND s.dur>0 GROUP BY p.name,t.name,t.tid ORDER BY cpuMs DESC`);
const states = query(`SELECT p.name AS package,s.state,SUM(s.dur)/1e6 AS durationMs
  FROM thread_state s JOIN thread t USING(utid) JOIN process p USING(upid)
  WHERE p.name LIKE 'org.aquariuslang.%' AND t.name='Aquarius-Wasm' AND s.dur>0 GROUP BY p.name,s.state`);
const stats = values => {
  const sorted = [...values].sort((a, b) => a - b);
  if (!sorted.length) return null;
  return {samples: sorted.length, meanMs: values.reduce((a, b) => a + b, 0) / values.length,
    minMs: sorted[0], medianMs: sorted[Math.floor(sorted.length / 2)], p95Ms: sorted[Math.ceil(sorted.length * .95) - 1], maxMs: sorted.at(-1)};
};
const packages = [...new Set(presents.map(p => p.package))].map(packageName => {
  const frames = presents.filter(p => p.package === packageName);
  const intervals = frames.slice(1).map((p, i) => (Number(p.ts) - Number(frames[i].ts)) / 1e6);
  const intervalStats = stats(intervals);
  return {package: packageName, pid: Number(frames[0].pid), presents: frames.length,
    intervalStats, presentationFps: intervalStats ? 1000 / intervalStats.meanMs : null,
    operations: operations.filter(p => p.package === packageName).map(p => ({operation: p.operation, count: Number(p.count), totalMs: Number(p.totalMs), meanMs: Number(p.meanMs)})),
    threadCpu: cpu.filter(p => p.package === packageName).map(p => ({thread: p.thread, tid: Number(p.tid), cpuMs: Number(p.cpuMs), percentOfOneCore: Number(p.cpuMs) / durationMs * 100})),
    runtimeThreadStates: states.filter(p => p.package === packageName).map(p => ({state: p.state, durationMs: Number(p.durationMs)})),
    presentEvents: frames.map(p => ({ts: Number(p.ts), durationNs: Number(p.dur)}))};
});
const result = {trace: path.resolve(trace), traceDurationMs: durationMs,
  method: 'Perfetto sched and gfx slices; median and nearest-rank p95 over consecutive QueuePresentKHR start timestamps. Per-operation durations can nest and must not be added.', packages};
const output = path.resolve(process.argv[4] ?? trace.replace(/\.perfetto-trace$/, '') + '-summary.json');
if (output === path.resolve(trace)) throw new Error('Output must differ from the input trace.');
fs.writeFileSync(output, JSON.stringify(result, null, 2) + '\n');
for (const p of packages) console.log(JSON.stringify({package: p.package, presents: p.presents, fps: p.presentationFps,
  intervals: p.intervalStats, operations: p.operations, topCpuThreads: p.threadCpu.slice(0, 3)}, null, 2));
console.log(`Saved ${output}`);
