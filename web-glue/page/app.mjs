// The page of an exported AquariusLang website: reads aquarius.json (written by `aqua build --target web`) and runs
// the application with the unified glue. ?autorun=0 waits for window.aquarius.run(); ?frames=N limits frame drivers.
import { startApplication } from './glue/index.mjs';

const output = document.getElementById('output'), error = document.getElementById('error');
const parameters = new URLSearchParams(location.search);
const state = { status: 'loading', exitCode: null, lines: [] };

async function run() {
    const manifest = await (await fetch('aquarius.json')).json();
    const controller = new AbortController();
    state.status = 'running';
    try {
        const result = await startApplication({
            program: new URL(manifest.program, location.href),
            base: new URL('runtime/', location.href),
            libraries: manifest.libraries,
            entry: manifest.entry,
            asset: async (name) => {
                const file = manifest.assets?.[name];
                if (!file) return null;
                const response = await fetch(file);
                if (!response.ok) throw new Error(`Cannot load asset ${name}: HTTP ${response.status}`);
                return new Uint8Array(await response.arrayBuffer());
            },
            environment: parameters.get('frames') ? { AQUARIUS_GRAPHICS_FRAMES: parameters.get('frames') } : {},
            output: (stream, line) => {
                state.lines.push(line);
                (stream === 2 ? error : output).textContent += line + '\n';
            },
            signal: controller.signal,
        });
        state.exitCode = result.exitCode;
        state.status = 'finished';
    } catch (failure) {
        state.status = 'failed';
        error.textContent += String(failure?.stack ?? failure) + '\n';
        throw failure;
    }
}

window.aquarius = { run, state };
if (parameters.get('autorun') !== '0') run();
