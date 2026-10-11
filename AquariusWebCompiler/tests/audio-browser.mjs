import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
const root=path.resolve(process.env.AQUARIUS_AUDIO_WEB??'../.web-build/audio-example/web');
const server=createServer(async(req,res)=>{try{const name=decodeURIComponent(new URL(req.url,'http://localhost').pathname);const file=path.resolve(root,'.'+name+(name.endsWith('/')?'index.html':''));if(!file.startsWith(root+path.sep))throw new Error('Outside root');res.setHeader('Content-Type',({'.html':'text/html','.mjs':'text/javascript','.json':'application/json','.wasm':'application/wasm'})[path.extname(file)]??'application/octet-stream');res.end(await readFile(file));}catch{res.statusCode=404;res.end();}});
await new Promise(r=>server.listen(0,'127.0.0.1',r));
const browser=await chromium.launch({channel:'chrome',headless:true,args:['--enable-unsafe-webgpu','--autoplay-policy=no-user-gesture-required']});
try{
  const page=await browser.newPage({viewport:{width:1120,height:860}});const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(`http://127.0.0.1:${server.address().port}/?autorun=0`);await page.waitForFunction(()=>window.aquarius);
  const check=await page.evaluate(()=>window.aquarius.run('音訊驗證.aqua',0));assert.ok(check.output.includes('音訊驗證全部通過'));
  await page.evaluate(()=>window.aquarius.run(undefined,2));await page.screenshot({path:path.join(root,'../音訊工坊.png')});
  // Headless Chrome on CI has no system audio clock. Its standard silent sink
  // still executes the real AudioWorklet in realtime without requiring speakers.
  await page.evaluate(()=>{const Context=AudioContext;globalThis.AudioContext=class extends Context{constructor(options){super({...options,sinkId:{type:'none'}});}};});
  const output=await page.evaluate(async()=>{
    const {BrowserAudioDevice}=await import('./audio.mjs');const module=await WebAssembly.compile(await(await fetch('./audio.wasm')).arrayBuffer());
    const device=await BrowserAudioDevice.create(module,false,new AbortController().signal);
    try{const id=await device.call('tone',440,1);await device.call('command',id,0,0);await device.start();await new Promise(r=>setTimeout(r,500));const playing=await device.call('query',id,0),position=await device.call('query',id,2),time=device.context.currentTime;await device.suspend();await device.call('command',id,2,0);await device.call('release',id);return {playing,position,time,state:device.context.state};}finally{await device.dispose();}
  });console.log(output);assert.equal(output.playing,1);assert.ok(output.position>0);assert.equal(output.state,'suspended');
  assert.deepEqual(errors,[]);console.log('PASS Traditional Chinese packaged audio, rendered example, realtime AudioWorklet, suspend and disposal');
  // Start the real Aquarius event loop, click a key, then cancel while audio is active.
  await page.evaluate(()=>{window.demo=window.aquarius.run(undefined,0).catch(e=>e.message);});await page.waitForFunction(()=>window.aquarius.host?.processing.frameCount>=2);
  await page.keyboard.press('1');await page.waitForFunction(()=>window.aquarius.host.audio.devices.size===1);
  await page.evaluate(()=>window.aquarius.stop());assert.deepEqual(errors,[]);console.log('PASS Aquarius callback playback and cancellation');
}finally{await browser.close();server.close();}
