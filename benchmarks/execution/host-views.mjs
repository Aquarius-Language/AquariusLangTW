import {readFileSync,writeFileSync} from 'node:fs';
import {performance} from 'node:perf_hooks';
import {WasmRuntime} from '../../AquariusCore/hosting/wasm-host.mjs';
import {readManifest} from '../../AquariusCore/hosting/wasm-manifest.mjs';

if(process.argv.length!==4)throw new Error('Usage: node host-views.mjs artifact.wasm output.json');
const compiledModule=new WebAssembly.Module(readFileSync(process.argv[2]));
const metadata=readManifest(compiledModule);
const vm=new WasmRuntime({bundle:{version:2,compiledModule,modules:metadata.modules,entry:metadata.entry,assets:{}}});
await vm.ready();
const session=vm.program.session,cached=session.view,uncached=function(){return new DataView(this.exports.memory.buffer);};
const address=session.call('aqua_allocate',1024*16),count=1_000_000,samples=[[],[]];
session.int(address,42);
for(let run=0;run<20;run++)for(let step=0;step<2;step++){
  const variant=(run+step)%2;session.view=variant?cached:uncached;
  let sum=0;const start=performance.now();
  for(let i=0;i<count;i++)sum+=session.int(address+(i&1023)*16);
  const elapsed=performance.now()-start;
  if(sum!==42*Math.ceil(count/1024))throw new Error('Incorrect cell checksum');
  if(run>=5)samples[variant].push(elapsed);
}
const medians=samples.map(values=>values.toSorted((a,b)=>a-b)[7]);
writeFileSync(process.argv[3],JSON.stringify({operation:'Read one i32 from each 16-byte guest value cell; alternating order, five warmups and fifteen samples',count,before_ms:medians[0],after_ms:medians[1],speedup:medians[0]/medians[1],samples_before_ms:samples[0],samples_after_ms:samples[1]},null,2)+'\n');
session.view=cached;
