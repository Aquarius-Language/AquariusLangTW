// Starts an AquariusLang application in the page: instantiates program.wasm, links the host core (aquarius.wasm),
// the C/C++ runtime libraries and the application's libraries into its memory, delivers its assets and drives the
// host's loop from the browser's event loop. The host core decides everything else.
import { Linker } from './loader.mjs';
import { wasiImports } from './wasi.mjs';
import { GlueContext, glueImports, registerGlue } from './glue.mjs';

registerGlue('core', (context) => ({
    now: () => performance.now(),
    write: (stream, pointer, length) => context.write(stream, context.bytes.slice(pointer, pointer + length)),
    wake: () => context.wake(),
    closing: () => context.closing?.(),
    // The page's settings for the runtime (frame limits, test switches): a malloc'd string, or 0.
    environment: (name) => {
        const value = context.environment?.[context.text(name)];
        return value === undefined || value === null ? 0 : context.allocate(String(value))[0];
    },
}));

const decoder = new TextDecoder();

/** Splits written bytes into lines for an output callback, decoding UTF-8 across writes. */
function lineWriter(output) {
    const pending = ['', '', ''];
    const decoders = [null, new TextDecoder(), new TextDecoder()];
    return {
        write(stream, bytes) {
            const text = pending[stream] + decoders[stream].decode(bytes, { stream: true });
            const lines = text.split('\n');
            pending[stream] = lines.pop();
            for (const line of lines) output(stream, line);
        },
        flush() { for (const stream of [1, 2]) if (pending[stream]) { output(stream, pending[stream]); pending[stream] = ''; } },
    };
}

/**
 * @param {{
 *   program: string | URL | ArrayBuffer | Uint8Array,   program.wasm or its bytes
 *   base: string | URL,                                  where aquarius.wasm, libc.so and libraries/*.wasm are served
 *   libraries?: string[],                                library side modules to link (names without .wasm)
 *   asset?: (name: string) => Promise<Uint8Array>,      bytes of an asset stored beside the program
 *   entry?: string, directory?: string,
 *   output?: (stream: number, line: string) => void,    1 output, 2 errors
 *   environment?: Object<string, string>,                values platform code reads as environment variables
 *   signal?: AbortSignal,
 * }} options
 * @returns {Promise<{exitCode: number}>} once the application finishes
 */
export async function startApplication(options) {
    const base = new URL(options.base, globalThis.location?.href);
    const fetchBytes = options.fetch ?? (async (url) => {
        const response = await fetch(url);
        if (!response.ok) throw new Error(`Cannot load ${url}: HTTP ${response.status}`);
        return new Uint8Array(await response.arrayBuffer());
    });
    const nextFrame = globalThis.requestAnimationFrame ?? ((callback) => setTimeout(callback, 16));
    const programBytes = options.program instanceof Uint8Array ? options.program
        : options.program instanceof ArrayBuffer ? new Uint8Array(options.program) : await fetchBytes(new URL(options.program, base));
    const program = await WebAssembly.compile(programBytes);

    const context = new GlueContext();
    context.program = program; // the compiled application module, which Thread workers instantiate again
    context.environment = options.environment ?? {};
    const writer = lineWriter(options.output ?? ((stream, line) => (stream === 2 ? console.error : console.log)(line)));
    context.write = (stream, bytes) => writer.write(stream, bytes);
    let service = () => { throw new Error('The host core is not linked yet.'); };
    const app = await WebAssembly.instantiate(program, { aquarius_v2: { service: (a, b, c, d, e, f, g) => service(a, b, c, d, e, f, g) } });
    context.memory = app.exports.memory;
    const glue = glueImports(context);
    const linker = new Linker({
        memory: app.exports.memory,
        reserve: (bytes, align) => {
            const pointer = app.exports.aqua_reserve(bytes + align);
            return Math.ceil(pointer / align) * align;
        },
        modules: { guest: app.exports, aqua_web: glue, wasi_snapshot_preview1: wasiImports({ memory: () => context.memory, write: context.write }) },
        fetchModule: async (name) => WebAssembly.compile(await fetchBytes(new URL(name.endsWith('.so') || name.endsWith('.wasm') ? name : name + '.wasm', base))),
    });
    context.linker = linker;
    // The guest runtime's heap must exist before modules reserve memory from it.
    app.exports.aqua_initialize(heapStartOf(program, programBytes));
    const host = (await linker.load('aquarius.wasm')).exports;
    service = host.aqua_web_service;
    const [modulePointer] = context.allocate(programBytes);
    const [directory] = context.allocate(options.directory ?? '');
    const error = host.aqua_web_create(modulePointer, programBytes.length, directory);
    if (error) throw new Error(context.text(error));
    // The libraries the application declares, plus any the page names, link before the program starts.
    const libraries = new Set(options.libraries ?? []);
    for (let i = 0; i < host.aqua_web_library_count(); i++) libraries.add(context.text(host.aqua_web_library_name(i)));
    for (const library of libraries) {
        const { exports } = await linker.load(`libraries/${library}.wasm`);
        const [name] = context.allocate(library);
        if (!exports.aqua_library_entry || !host.aqua_web_add_library(name, linker.tableIndex(exports.aqua_library_entry)))
            throw new Error(`The library ${library} does not support this runtime.`);
    }
    const assetCount = host.aqua_web_asset_count();
    await Promise.all(Array.from({ length: assetCount }, async (_, i) => {
        const name = context.text(host.aqua_web_asset_name(i));
        const bytes = options.asset ? await options.asset(name) : null;
        if (!bytes) return;
        const [pointer] = context.allocate(bytes);
        host.aqua_web_asset_set(i, pointer, bytes.length);
    }));

    const [entry] = context.allocate(options.entry ?? '');
    return new Promise((resolve, reject) => {
        let scheduled = false, finished = false;
        const channel = new MessageChannel();
        const finish = () => {
            if (finished) return;
            finished = true;
            channel.port1.close();
            const exitCode = host.aqua_web_exit_code();
            writer.flush();
            host.aqua_web_destroy();
            resolve({ exitCode });
        };
        const turn = () => {
            scheduled = false;
            if (finished) return;
            if (options.signal?.aborted) host.aqua_web_cancel();
            let wait;
            try { wait = host.aqua_web_turn(12); }
            catch (failure) { finished = true; channel.port1.close(); writer.flush(); reject(failure); return; }
            if (wait < 0) finish();
            else schedule(wait);
        };
        channel.port1.onmessage = turn;
        const schedule = (wait) => {
            if (scheduled || finished) return;
            scheduled = true;
            if (wait <= 0) channel.port2.postMessage(0);
            else if (wait < 34) nextFrame(turn); // frame drivers: align with the display
            else if (wait < 1e8) setTimeout(turn, wait);
            else scheduled = false;                         // only waiting for events: context.wake() schedules
        };
        context.wake = () => schedule(0);
        options.signal?.addEventListener('abort', () => schedule(0));
        if (host.aqua_web_start(entry) !== 0) { writer.flush(); resolve({ exitCode: 1 }); return; }
        schedule(0);
    });
}

/** heapStart from the application's metadata: guest data ends there and its heap begins. */
function heapStartOf(module, bytes) {
    const section = new Uint8Array(WebAssembly.Module.customSections(module, 'aquarius.application')[0]);
    let p = 5; // 00 'AQM' version
    const leb = () => { let r = 0, s = 0, b; do { b = section[p++]; r |= (b & 127) << s; s += 7; } while (b & 128); return r >>> 0; };
    leb(); leb(); // value ABI, program address
    return leb();
}
