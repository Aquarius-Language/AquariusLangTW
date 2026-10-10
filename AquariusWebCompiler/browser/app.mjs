import {BrowserHost} from './host.mjs';
import {inspect} from './values.mjs';
const output=document.getElementById('output'),error=document.getElementById('error');
let bundle,host,controller,execution;
function showError(e){error.textContent=e.message;error.hidden=false;}
async function stop(){const previous=host;if(previous){previous.processing.exiting=true;controller.abort();await execution?.catch(()=>{});await previous.dispose();if(host===previous){host=null;window.aquarius.host=null;}}window.aquarius.state='stopped';}
async function run(entry=bundle.entry,frames=0){
  await stop();document.getElementById('surfaces').replaceChildren();output.textContent='';error.hidden=true;
  controller=new AbortController();const current=new BrowserHost(bundle,{signal:controller.signal,frameLimit:frames,print:s=>{output.textContent+=s+'\n';console.log(s);}});
  host=current;window.aquarius.host=host;window.aquarius.state='running';execution=current.execute(entry);
  try{const result=await execution;if(host===current){window.aquarius.state='finished';if(result!==undefined)output.textContent+=inspect(result)+'\n';}return {result:inspect(result),output:output.textContent};}
  catch(e){if(host===current&&!current.signal.aborted){window.aquarius.state='error';output.textContent+=e.message+'\n';showError(e);}throw e;}
}
try{
  const response=await fetch('./program.json');if(!response.ok)throw new Error(`Could not load program (${response.status})`);
  bundle=await response.json();const wasm=await fetch(bundle.wasm);if(!wasm.ok)throw new Error(`Could not load WebAssembly (${wasm.status})`);bundle.compiledModule=await WebAssembly.compile(await wasm.arrayBuffer());window.aquarius={bundle,run,stop,host:null,state:'ready'};
  // Developer/test hooks remain available without adding controls to the game.
  const params=new URLSearchParams(location.search);
  if(params.get('autorun')!=='0')run(bundle.entry,Number(params.get('frames')??0)).catch(e=>{if(!controller?.signal.aborted){showError(e);console.error(e);}});
}catch(e){showError(e);console.error(e);}
