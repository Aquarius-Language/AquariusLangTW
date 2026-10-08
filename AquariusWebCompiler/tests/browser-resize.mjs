import assert from 'node:assert/strict';
import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const build=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../../.web-build');
const mime={'.html':'text/html','.mjs':'text/javascript','.json':'application/json','.wasm':'application/wasm'};
const server=createServer(async(req,res)=>{
  try{const requested=new URL(req.url,'http://localhost').pathname,file=path.resolve(build,'.'+requested+(requested.endsWith('/')?'index.html':''));
    if(!file.startsWith(build+path.sep))throw new Error('Outside build');
    res.setHeader('Content-Type',mime[path.extname(file)]??'application/octet-stream');res.end(await readFile(file));
  }catch(e){res.statusCode=404;res.end(e.message);}
});
await new Promise(r=>server.listen(0,'127.0.0.1',r));
const base=`http://127.0.0.1:${server.address().port}`;
let browser;
try{
  browser=await chromium.launch({channel:process.env.AQUARIUS_BROWSER??'chrome',headless:true,args:['--enable-unsafe-webgpu']});
  const context=await browser.newContext({viewport:{width:640,height:480},deviceScaleFactor:2}),page=await context.newPage(),errors=[];
  page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});
  // No query flags or developer run hook: the bottle entry must launch itself.
  await page.goto(`${base}/resize-bottle/`);
  await page.waitForFunction(()=>window.aquarius?.host?.processing.frameCount===1);
  const dimensions=()=>page.evaluate(()=>{
    const p=window.aquarius.host.processing,c=p.screen,r=p.dom.getBoundingClientRect();
    return {logical:[c.width,c.height],pixels:[c.pixelWidth,c.pixelHeight],target:[c.target.width,c.target.height],canvas:[p.dom.width,p.dom.height],bounds:[r.x,r.y,r.width,r.height],frames:p.frameCount};
  });
  let state=await dimensions();assert.deepEqual(state.logical,[640,480]);assert.deepEqual(state.pixels,[1280,960]);assert.deepEqual(state.target,state.pixels);assert.deepEqual(state.canvas,state.pixels);assert.deepEqual(state.bounds,[0,0,640,480]);
  assert.equal(await page.locator('header,select,button').count(),0);
  assert.equal(await page.evaluate(()=>document.documentElement.scrollHeight===innerHeight&&document.documentElement.scrollWidth===innerWidth),true);
  await page.evaluate(()=>{
    const h=window.aquarius.host,p=h.processing;
    window.resizeEvents=[];
    p.events.set('windowResized',async()=>{
      const c=p.screen;
      // Exercise drawing/readback inside the callback, before the draw callback.
      await p.module.scope.get('background')(0,255,0);
      const corner=await p.module.scope.get('get')(c.width-1,c.height-1);
      window.resizeEvents.push({size:[c.width,c.height,c.pixelWidth,c.pixelHeight],target:[c.target.width,c.target.height],corner:corner.value});
    });
  });
  await page.setViewportSize({width:900,height:500});
  await page.waitForFunction(()=>window.aquarius.host.processing.screen.width===900&&window.aquarius.host.processing.frameCount===2);
  state=await dimensions();assert.deepEqual(state.pixels,[1800,1000]);assert.deepEqual(state.target,state.pixels);assert.deepEqual(state.bounds,[0,0,900,500]);
  let events=await page.evaluate(()=>window.resizeEvents);assert.equal(events.length,1);assert.equal(events[0].corner,0xff00ff00);assert.deepEqual(events[0].target,[1800,1000]);
  // A density-only change must recreate pixels/text without resetting the camera.
  const view=await page.evaluate(()=>{const c=window.aquarius.host.processing.screen;c.view[12]=1;return Array.from(c.view);});
  const cdp=await context.newCDPSession(page);
  await cdp.send('Emulation.setDeviceMetricsOverride',{width:900,height:500,deviceScaleFactor:1.25,mobile:false});
  await page.waitForFunction(()=>window.aquarius.host.processing.screen.pixelWidth===1125&&window.aquarius.host.processing.frameCount===3);
  state=await dimensions();assert.deepEqual(state.logical,[900,500]);assert.deepEqual(state.pixels,[1125,625]);assert.deepEqual(state.target,state.pixels);
  assert.deepEqual(await page.evaluate(()=>Array.from(window.aquarius.host.processing.screen.view)),view);
  assert.equal(await page.evaluate(()=>window.aquarius.host.processing.screen.textRasterScale(10,30)),1.25);
  const text=await page.evaluate(()=>[...window.aquarius.host.processing.textCache.values()].map(t=>[t.width,t.texture.width]));
  assert.ok(text.some(([logical,physical])=>physical===Math.ceil(logical*1.25)));
  const pixels=await page.evaluate(async()=>{
    const scope=window.aquarius.host.processing.module.scope;
    return [(await scope.get('get')(10,490)).value,(await scope.get('get')(890,490)).value,(await scope.get('get')(885,485)).value];
  });assert.deepEqual(pixels,[0xff00ff00,0xffff0000,0xff0000ff]);
  // Input remains in logical coordinates at fractional DPR.
  await page.mouse.move(450,250);
  await page.waitForFunction(()=>window.aquarius.host.processing.module.scope.get('mouseX').value===450);
  assert.equal(await page.evaluate(()=>window.aquarius.host.processing.module.scope.get('mouseY').value),250);
  const duringResize=await page.evaluate(async()=>{
    const p=window.aquarius.host.processing,area=document.getElementById('surfaces');
    area.style.width='750px';
    area.dispatchEvent(new PointerEvent('pointermove',{clientX:300,clientY:200}));
    await p.beginFrame();
    const position=[p.module.scope.get('mouseX').value,p.module.scope.get('mouseY').value];
    area.style.width='';await p.beginFrame();return position;
  });assert.deepEqual(duringResize,[300,200]);
  // Hide/restore: no zero-sized GPU textures, keep logical camera, redraw on restore.
  await page.evaluate(()=>document.getElementById('surfaces').style.display='none');
  await page.waitForFunction(()=>window.aquarius.host.processing.screen.pixelWidth===0);
  assert.deepEqual((await dimensions()).logical,[900,500]);
  await page.evaluate(()=>document.getElementById('surfaces').style.display='');
  await page.waitForFunction(()=>window.aquarius.host.processing.screen.pixelWidth===1125);
  await page.evaluate(()=>window.aquarius.stop());
  assert.equal(await page.locator('#surfaces canvas,textarea').count(),0);
  assert.deepEqual(errors,[]);
  console.log('PASS bottle autorun, viewport, paused resize, callback readback, fractional DPR, text, input, hidden/restore, disposal');
  // Confirm that the complete marble game resizes its offscreen 3D target too.
  await cdp.send('Emulation.clearDeviceMetricsOverride');
  await page.goto(`${base}/marble-bottle/`);
  await page.waitForFunction(()=>window.aquarius?.host?.processing.frameCount>=2);
  await page.setViewportSize({width:720,height:540});
  await page.waitForFunction(()=>{
    const p=window.aquarius.host.processing;
    return p.screen.width===720&&[...p.canvases].every(c=>c.width===720&&c.pixelWidth===1440&&c.target.width===1440);
  });
  await page.screenshot({path:path.join(build,'marble-viewport-resize.png')});
  await page.evaluate(()=>window.aquarius.stop());assert.deepEqual(errors,[]);
  console.log('PASS marble bottle autorun and offscreen 3D target resize');
  await page.goto(`${base}/examples-bottle/?autorun=0`);
  await page.waitForFunction(()=>!!window.aquarius);
  await page.evaluate(()=>{window.glTask=window.aquarius.run('opengl_cube/main.aqua').catch(()=>{});});
  await page.waitForFunction(()=>!!document.querySelector('#surfaces canvas'));
  await page.setViewportSize({width:800,height:600});
  await page.waitForFunction(()=>{const c=document.querySelector('#surfaces canvas');return c.width===1600&&c.height===1200;});
  const gl=await page.evaluate(async()=>{
    const h=window.aquarius.host,scope=h.modules.get('GLFW').scope;
    return {logical:(await scope.get('GetWindowSize')(null)).map(n=>n.value),pixels:(await scope.get('GetFramebufferSize')(null)).map(n=>n.value),error:(await h.modules.get('GL').scope.get('glGetError')()).value};
  });assert.deepEqual(gl,{logical:[800,600],pixels:[1600,1200],error:0});
  await page.evaluate(()=>window.aquarius.stop());assert.deepEqual(errors,[]);
  console.log('PASS WebGL framebuffer resize through the same host surface');
}finally{await browser?.close();server.close();}
