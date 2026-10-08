import {build} from 'esbuild';
import {copyFile} from 'node:fs/promises';
import {existsSync} from 'node:fs';
import {createRequire} from 'node:module';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..'),require=createRequire(path.join(root,'package.json'));
for(const name of ['gl-matrix','earcut'])await build({entryPoints:[name==='gl-matrix'?path.join(path.dirname(require.resolve('gl-matrix/package.json')),'esm/index.js'):require.resolve(name)],bundle:true,format:'esm',outfile:path.join(root,`browser/vendor-${name}.mjs`),minify:true});
const jolt=path.dirname(require.resolve('jolt-physics/package.json'));
await copyFile(path.join(jolt,'dist/jolt-physics.wasm.js'),path.join(root,'browser/vendor-jolt.mjs'));
await copyFile(path.join(jolt,'dist/jolt-physics.wasm.wasm'),path.join(root,'browser/vendor-jolt.wasm'));
await copyFile(path.join(jolt,'LICENSE'),path.join(root,'browser/vendor-jolt.LICENSE.txt'));
for(const name of ['earcut','gl-matrix']){
  let dir=path.dirname(require.resolve(name));
  while(!existsSync(path.join(dir,'LICENSE')))dir=path.dirname(dir);
  await copyFile(path.join(dir,'LICENSE'),path.join(root,`browser/vendor-${name}.LICENSE.txt`));
}
console.log('Prepared pinned browser dependencies and licenses.');
