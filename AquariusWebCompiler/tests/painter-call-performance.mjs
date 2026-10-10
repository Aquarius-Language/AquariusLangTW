// Optional real-WebGPU A/B benchmark. Reads an existing Painter export without
// modifying it; only vm.mjs/host.mjs are served from the candidate runtime.
// node tests/painter-call-performance.mjs <Painter directory> [output directory]
import assert from 'node:assert/strict';
import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const repository=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const painter=path.resolve(process.argv[2]??'../AquariusLang_Painter');
const output=path.resolve(process.argv[3]??path.join(repository,'.web-build/call-performance'));
const website=path.join(painter,'web'),runtime=path.join(repository,'AquariusWebCompiler/browser');
await mkdir(output,{recursive:true});
const mime={'.mjs':'text/javascript','.html':'text/html','.json':'application/json','.wasm':'application/wasm'};
const server=createServer(async(req,res)=>{
  try{
    const url=new URL(req.url,'http://localhost'),relative=decodeURIComponent(url.pathname).replace(/^\//,'')||'index.html';
    let file=path.resolve(website,relative);
    if(!file.startsWith(website+path.sep))throw new Error('Outside website');
    if(url.searchParams.get('candidate')==='1'&&['vm.mjs','host.mjs'].includes(relative))file=path.join(runtime,relative);
    res.setHeader('Content-Type',mime[path.extname(file)]??'application/octet-stream');
    res.end(await readFile(file));
  }catch(error){res.statusCode=404;res.end(error.message);}
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const browser=await chromium.launch({channel:process.env.AQUARIUS_BROWSER??'chrome',headless:true,args:['--enable-unsafe-webgpu']});
const results=[];
try{
  for(const candidate of [false,true]){
    const page=await browser.newPage({viewport:{width:1440,height:900}}),errors=[];
    page.on('pageerror',error=>errors.push(error.message));
    page.on('console',message=>{if(message.type()==='error')errors.push(message.text());});
    // Module dependency URLs have no query parameters, so propagate the mode.
    if(candidate)await page.route('**/*',async route=>{
      const url=new URL(route.request().url());
      if(url.origin===`http://127.0.0.1:${server.address().port}`&&['/vm.mjs','/host.mjs'].includes(url.pathname)){
        url.searchParams.set('candidate','1');await route.continue({url:url.href});
      }else await route.continue();
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    try{
      await page.waitForFunction(()=>window.aquarius?.state==='error'||window.aquarius?.host?.processing?.events.get('mousePressed')?.env.get('已繪製').value>0);
      assert.equal(await page.evaluate(()=>window.aquarius.state),'running',errors.join('\n'));
    }catch(error){
      const state=await page.evaluate(()=>({state:window.aquarius?.state,error:document.getElementById('error')?.textContent,output:document.getElementById('output')?.textContent}));
      throw new Error(`Painter startup failed: ${JSON.stringify({state,errors})}`,{cause:error});
    }
    try{await page.waitForFunction(()=>(window.aquarius.host.vm.作業數??0)===0&&!window.aquarius.host.processing.處理事件&&window.aquarius.host.processing.eventQueue.length===0);}
    catch(error){throw new Error(`Painter did not become idle: ${JSON.stringify(await page.evaluate(()=>({state:window.aquarius.state,jobs:window.aquarius.host.vm.作業數,events:window.aquarius.host.processing.處理事件,queue:window.aquarius.host.processing.eventQueue.length,looping:window.aquarius.host.processing.looping,error:document.getElementById('error').textContent})))}`,{cause:error});}
    // Finish the host animation loop before invoking manual frames. Otherwise
    // baseline Promise yields let its next beginFrame reset a live matrix stack.
    await page.evaluate(()=>{window.aquarius.host.processing.exiting=true;});
    await page.waitForFunction(()=>!window.aquarius.host.processing.running);
    await page.evaluate(()=>{window.aquarius.host.processing.exiting=false;});
    const samples=await page.evaluate(async()=>{
      const h=window.aquarius.host,p=h.processing,env=p.events.get('mousePressed').env,engine=env.get('引擎').scope;
      const n=value=>({type:Number.isInteger(value)?'int':'double',value});
      const invoke=(scope,name,args=[])=>h.vm.invoke(scope.get(name),args.map(value=>typeof value==='number'?n(value):value));
      const measure=async(name,action)=>{
        for(let i=0;i<3;i++)await action();
        const times=[];
        for(let i=0;i<9;i++){const start=performance.now();await action();times.push(performance.now()-start);}
        const ordered=[...times].sort((a,b)=>a-b);
        return {name,medianMs:ordered[4],samplesMs:times};
      };
      p.looping=false;p.redraw=false;
      const result=[];
      result.push(await measure('full redraw 1440x900, document 960x600',async()=>{
        await p.beginFrame();await invoke(env,'繪製');await p.endFrame();await p.device.gpu.queue.onSubmittedWorkDone();
      }));
      await invoke(engine,'新作品',[512,320]);
      for(const brush of [0,3,8]){
        await invoke(engine,'設定參數',['筆刷',brush]);
        await invoke(engine,'設定參數',['粗細',8]);
        result.push(await measure(`brush ${brush}, 40px stroke, width 8`,async()=>{
          await p.module.scope.get('randomSeed')(n(123));
          await invoke(engine,'筆畫',[40,40,80,40,false]);
          await p.device.gpu.queue.onSubmittedWorkDone();
        }));
      }
      const layers=engine.get('圖層'),canvas=h.objects.get(layers[0][0]),pixels=await canvas.target.readPixels();
      if(!pixels.some((value,i)=>i%4!==3&&value<200))throw new Error('Brush benchmark did not produce pixels');
      return result;
    });
    assert.deepEqual(errors,[]);
    results.push({variant:candidate?'candidate':'shipped',samples});
    for(const sample of samples)console.log(`${candidate?'candidate':'shipped'}: ${sample.name}: ${sample.medianMs.toFixed(2)} ms`);
    await page.evaluate(()=>window.aquarius.stop());
    await page.close();
  }
}finally{
  await browser.close();server.close();
  await writeFile(path.join(output,'painter-browser-performance.json'),JSON.stringify({browser:process.env.AQUARIUS_BROWSER??'chrome',results},null,2));
}
