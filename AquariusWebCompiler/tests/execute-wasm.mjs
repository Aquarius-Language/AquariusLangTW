import {readFile} from 'node:fs/promises';
import {inspect} from '../browser/values.mjs';
import {WasmRuntime} from '../browser/wasm.mjs';
const compiledModule=await WebAssembly.compile(await readFile(process.argv[2]));
const runtime=new WasmRuntime({bundle:{compiledModule},builtins:new Map()});
const program=await runtime.ready();console.log(inspect(await runtime.execute(program.metadata.modules[program.metadata.entry])));
