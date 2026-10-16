import assert from 'node:assert/strict';
import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import path from 'node:path';
const directory=path.resolve(process.argv[2]??'.web-build/threading');
const server=createServer(async(req,res)=>{try{
  const name=decodeURIComponent(new URL(req.url,'http://localhost').pathname),file=path.resolve(directory,'.'+name);
  if(!file.startsWith(directory+path.sep))throw Error('Invalid path');
  res.setHeader('Content-Type',path.extname(file)==='.wasm'?'application/wasm':path.extname(file)==='.mjs'?'text/javascript':'text/html');
  res.end(await readFile(file));
}catch{res.statusCode=404;res.end();}});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
let browser;
try{
  browser=await chromium.launch({channel:process.env.AQUARIUS_BROWSER??'chrome',headless:true});
  const page=await browser.newPage();await page.goto(`http://127.0.0.1:${server.address().port}/index.html?autorun=0`);
  const result=await page.evaluate(async()=>{
    const {WasmRuntime}=await import('./wasm-host.mjs'),{inspect}=await import('./values.mjs');
    const compiledModule=await WebAssembly.compile(await(await fetch('./program.wasm')).arrayBuffer());
    const metadata=JSON.parse(new TextDecoder().decode(WebAssembly.Module.customSections(compiledModule,'aquarius.application')[0]));
    const output=[],vm=new WasmRuntime({bundle:{compiledModule},builtins:new Map([['print',(...args)=>{output.push(args.map(inspect).join(' '));}]])});
    let ticks=0;const timer=setInterval(()=>ticks++,1);
    try{return {result:inspect(await vm.execute(metadata.modules[metadata.entry])),output,ticks,isolated:crossOriginIsolated};}
    finally{clearInterval(timer);await vm.dispose();}
  });
  assert.equal(result.result,'1001000');assert.equal(result.output[0],'1001000');assert.ok(result.ticks>0);assert.equal(result.isolated,false);
  console.log('PASS real Chromium module workers, function parameters, captured snapshots, responsive waits, cleanup, no isolation headers');
}finally{await browser?.close();await new Promise(resolve=>server.close(resolve));}
