import {mkdtempSync,mkdirSync,writeFileSync,readFileSync,rmSync,existsSync} from 'node:fs';
import {homedir} from 'node:os';
import {execFileSync} from 'node:child_process';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {WasmRuntime} from '../browser/wasm.mjs';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const compiler=process.env.AQUARIUS_COMPILER_DLL??path.join(root,'AquariusCli/bin/Debug/net8.0/aqua.dll');
const local=path.join(homedir(),'.dotnet',process.platform==='win32'?'dotnet.exe':'dotnet');
const dotnet=process.env.DOTNET_HOST_PATH??(existsSync(local)?local:'dotnet');
const cache=new Map();
export function compile(modules,entry='main.aqua') {
  const key=JSON.stringify([modules,entry]);if(cache.has(key))return cache.get(key);
  const directory=path.join(root,'.web-build/wasm-fixtures');mkdirSync(directory,{recursive:true});
  const temp=mkdtempSync(path.join(directory,'test-'));
  try {
    const sources=Object.entries(modules).map(([name,source])=>{const file=path.join(temp,name);mkdirSync(path.dirname(file),{recursive:true});writeFileSync(file,source);return file;});
    const output=path.join(temp,'program.wasm');
    execFileSync(dotnet,[compiler,'build',...sources,'--root',temp,'--entry',entry,'-o',output],{encoding:'utf8',timeout:30000});
    const compiledModule=new WebAssembly.Module(readFileSync(output));
    const metadata=JSON.parse(new TextDecoder().decode(WebAssembly.Module.customSections(compiledModule,'aquarius.application')[0]));
    const bundle={version:2,compiledModule,modules:metadata.modules,entry,assets:{}};
    cache.set(key,bundle);return bundle;
  } finally {if(path.dirname(path.resolve(temp))!==path.resolve(directory))throw new Error('Invalid fixture cleanup path');rmSync(temp,{recursive:true,force:true});}
}
export async function runtime(source,builtins=new Map(),options={}) {
  const bundle=compile({'main.aqua':source}),vm=new WasmRuntime({bundle,builtins,...options});
  await vm.ready();return {vm,index:bundle.modules['main.aqua']};
}
