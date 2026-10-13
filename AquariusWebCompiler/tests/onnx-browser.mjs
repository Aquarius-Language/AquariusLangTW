import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
const repository=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const root=path.resolve(process.env.AQUARIUS_ONNX_WEB??path.join(repository,'../AquariusLangTW_MediaPipe/web'));
const report=path.join(repository,'.web-build/onnx');await mkdir(report,{recursive:true});
const fixtures=process.env.AQUARIUS_POSE_FIXTURES;
const server=createServer(async(req,res)=>{
  try {
    const request=decodeURIComponent(new URL(req.url,'http://localhost').pathname);
    if(fixtures&&request.startsWith('/fixtures/')){
      const file=path.resolve(fixtures,'.'+request.slice(9));if(!file.startsWith(path.resolve(fixtures)+path.sep))throw new Error('Outside fixtures');
      res.setHeader('Content-Type',path.extname(file)==='.jpg'?'image/jpeg':path.extname(file)==='.png'?'image/png':'application/json');res.end(await readFile(file));return;
    }
    if(!request.startsWith('/project/'))throw new Error('Bad prefix');
    const file=path.resolve(root,'.'+request.slice(8)+(request.endsWith('/')?'index.html':''));
    if(!file.startsWith(root+path.sep))throw new Error('Outside build');
    res.setHeader('Content-Type',({'.html':'text/html','.mjs':'text/javascript','.json':'application/json','.wasm':'application/wasm'})[path.extname(file)]??'application/octet-stream');
    res.end(await readFile(file));
  } catch {res.statusCode=404;res.end('Missing file');}
});
await new Promise(r=>server.listen(0,'127.0.0.1',r));
const browser=await chromium.launch({channel:process.env.AQUARIUS_BROWSER??'chrome',headless:true,args:['--enable-unsafe-webgpu','--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream',...(fixtures?['--use-file-for-fake-video-capture='+path.join(fixtures,'pose.y4m')]:[])]});
const errors=[];
try {
  const context=await browser.newContext({permissions:['camera'],viewport:{width:1040,height:760}});
  const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
  page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});
  await page.goto(`http://127.0.0.1:${server.address().port}/project/?autorun=0`);await page.waitForFunction(()=>window.aquarius);
  const initialization=await page.evaluate(async()=>{
    const ort=await import('./ort.webgpu.min.mjs');ort.env.wasm.numThreads=1;ort.env.wasm.wasmPaths=new URL('./',location.href).href;
    const bytes=Uint8Array.from(atob(window.aquarius.bundle.assets['模型/姿態.onnx']),c=>c.charCodeAt(0));
    try{const s=await ort.InferenceSession.create(bytes,{executionProviders:['wasm']});await s.release();return 'ready';}catch(e){return String(e.stack??e);}
  });assert.equal(initialization,'ready');
  const cpu=await page.evaluate(()=>window.aquarius.run('姿態驗證.aqua'));assert.match(cpu.output,/姿態模型推論驗證通過/);
  const gpu=await page.evaluate(()=>window.aquarius.run('圖形加速驗證.aqua'));assert.match(gpu.output,/圖形加速姿態推論驗證通過/);
  const parity=await page.evaluate(async()=>{
    const {BrowserOnnxBackend,createTensor}=await import('./onnx.mjs');
    const backend=new BrowserOnnxBackend(),bytes=window.aquarius.host.asset('/模型/姿態.onnx');
    const cpu=await backend.createSession(bytes,'CPU'),gpu=await backend.createSession(bytes,'WebGPU');
    try {
      const input=cpu.inputs[0],outputs=cpu.outputs.filter(m=>[1,195].includes(m.shape.reduce((a,b)=>a*b,1))).map(m=>m.name);
      const feeds=new Map([[input.name,createTensor('float32',input.shape,Array(256*256*3).fill(0.25))]]);
      const expected=await cpu.run(feeds,outputs),actual=await gpu.run(feeds,outputs);let maxError=0,valid=true;
      for(const name of outputs){const a=expected.get(name).data,b=actual.get(name).data;for(let i=0;i<a.length;i++){const delta=Math.abs(a[i]-b[i]);maxError=Math.max(maxError,delta);if(!Number.isFinite(b[i])||delta>0.05+Math.abs(a[i])*0.01)valid=false;}}
      return {outputs,maxError,valid};
    } finally {await backend.dispose();}
  });
  assert.equal(parity.valid,true,JSON.stringify(parity));assert.equal(parity.outputs.length,2);
  const accuracy=fixtures?await page.evaluate(async()=>{
    const {BrowserOnnxBackend}=await import('./onnx.mjs'),{BrowserPoseTracker}=await import('./mediapipe.mjs');
    const bitmap=await createImageBitmap(await(await fetch('/fixtures/pose.jpg')).blob()),canvas=document.createElement('canvas');canvas.width=bitmap.width;canvas.height=bitmap.height;
    const ctx=canvas.getContext('2d');ctx.drawImage(bitmap,0,0);bitmap.close();const image={width:canvas.width,height:canvas.height,pixels:ctx.getImageData(0,0,canvas.width,canvas.height).data,alpha:'Opaque'};
    const expected=(await(await fetch('/fixtures/native-results.json')).json())[0].landmarks;
    const host=window.aquarius.host,backend=new BrowserOnnxBackend(),reports=[];
    try{for(const provider of ['CPU','WebGPU']){
      const tracker=await BrowserPoseTracker.create(backend,host.asset('/模型/人物偵測.onnx'),host.asset('/模型/姿態.onnx'),provider);
      try{const first=await tracker.analyze(image,0);let error=0;
        if(!first.hasPose)throw new Error(provider+': no person detected');
        for(let i=0;i<33;i++)error+=Math.hypot((first.landmarks[i*5]-expected[i*5])*image.width,(first.landmarks[i*5+1]-expected[i*5+1])*image.height);
        const frames=[];for(let i=1;i<=12;i++){const frame=await tracker.analyze(image,i*33);if(!frame.hasPose||frame.detectionRan)throw new Error('Tracking failed');frames.push(frame.totalMilliseconds);}
        reports.push({provider,desktopParityMeanPixels:error/33,firstFrameMs:first.totalMilliseconds,trackedMedianMs:frames.sort((a,b)=>a-b)[6]});
        tracker.reset();const screenshot=await createImageBitmap(await(await fetch('/fixtures/closeup.png')).blob());canvas.width=screenshot.width;canvas.height=screenshot.height;ctx.drawImage(screenshot,0,0);screenshot.close();
        const closeup=await tracker.analyze({width:canvas.width,height:canvas.height,pixels:ctx.getImageData(0,0,canvas.width,canvas.height).data},450);
        reports.at(-1).closeup={hasPose:closeup.hasPose,confidence:closeup.confidence,landmarks:closeup.landmarks};
        tracker.reset();const blank=await tracker.analyze({width:640,height:480,pixels:new Uint8Array(640*480*4)},500);if(blank.hasPose||blank.landmarks.length)throw new Error('Blank false positive');
      }finally{await tracker.dispose();}
    }}finally{await backend.dispose();}return reports;
  }):[];
  for(const r of accuracy)assert.ok(r.desktopParityMeanPixels<3,JSON.stringify(r));
  await page.evaluate(()=>{window.poseTask=window.aquarius.run('星泉姿態觀測站.aqua').catch(e=>{window.poseError=e.message;});});
  await page.waitForFunction(()=>window.aquarius.state==='error'||window.aquarius.host?.processing.frameCount>=2);
  assert.equal(await page.evaluate(()=>window.poseError??document.getElementById('error').textContent),'');
  await page.mouse.click(790,250);
  await page.waitForFunction(()=>window.aquarius.host?.onnx.sessions.size===2&&window.aquarius.host?.webcam.sessions.size===1&&typeof [...window.aquarius.host.webcam.sessions][0]?.capture==='function',null,{timeout:60000});
  const frame=await page.evaluate(()=>window.aquarius.host.processing.frameCount);
  const timing=await page.evaluate(()=>{
    const host=window.aquarius.host;window.poseProfile={capture:[],analyze:[],draw:[],presentation:[],start:performance.now()};
    const wrap=(obj,name,key)=>{const original=obj[name].bind(obj);obj[name]=async(...args)=>{const start=performance.now();try{return await original(...args);}finally{window.poseProfile[key].push(performance.now()-start);}};};
    wrap([...host.webcam.sessions][0],'capture','capture');wrap([...host.pose.trackers][0],'analyze','analyze');wrap(host.processing,'endFrame','presentation');
  });
  await page.waitForFunction(frame=>window.aquarius.host.processing.frameCount>=frame+22,frame,{timeout:60000});
  const profile=await page.evaluate(frame=>{const p=window.poseProfile,out={framesPerSecond:(window.aquarius.host.processing.frameCount-frame)*1000/(performance.now()-p.start)};for(const key of ['capture','analyze','presentation'])out[key+'MedianMs']=p[key].sort((a,b)=>a-b)[Math.floor(p[key].length/2)];return out;},frame);
  await page.screenshot({path:path.join(report,'browser.png')});
  await page.mouse.click(790,395);
  await page.waitForFunction(()=>window.aquarius.host.onnx.sessions.size===0&&window.aquarius.host.webcam.sessions.size===0);
  await page.evaluate(()=>window.aquarius.stop());assert.equal(await page.locator('canvas,textarea').count(),0);assert.deepEqual(errors,[]);
  const result={passed:true,cpu:cpu.output,gpu:gpu.output,parity,accuracy,profile,cameraPreview:true,cleanup:true};
  await writeFile(path.join(report,'browser-results.json'),JSON.stringify(result,null,2));
  console.log(JSON.stringify({...result,accuracy:accuracy.map(r=>({...r,closeup:r.closeup?{hasPose:r.closeup.hasPose,confidence:r.closeup.confidence}:undefined}))}));
} finally {await browser.close();await new Promise(r=>server.close(r));}
