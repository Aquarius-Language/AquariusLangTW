// Compile the real sibling projects with scripts/test-projects.ps1 first.
import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFile,writeFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
const root=path.resolve(process.env.AQUARIUS_PROJECT_BUILD??path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../../.web-build/projects')),results=[];
const server=createServer(async(req,res)=>{
  try {const pathname=decodeURIComponent(new URL(req.url,'http://localhost').pathname),file=path.resolve(root,'.'+pathname+(pathname.endsWith('/')?'index.html':''));
    if(!file.startsWith(root+path.sep))throw Error('Outside project output');
    res.setHeader('Content-Type',({'.html':'text/html','.mjs':'text/javascript','.json':'application/json','.wasm':'application/wasm'})[path.extname(file)]??'application/octet-stream');res.end(await readFile(file));
  }catch(error){res.statusCode=404;res.end(error.message);}
});
await new Promise(r=>server.listen(0,'127.0.0.1',r));
const browser=await chromium.launch({channel:process.env.AQUARIUS_BROWSER??'chrome',headless:true,args:['--enable-unsafe-webgpu']});
try {for(const project of ['marble','painter','snake']) {
  const page=await browser.newPage({viewport:{width:1440,height:900}}),errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});
  try {
    await page.goto(`http://127.0.0.1:${server.address().port}/${project}/?autorun=0`);await page.waitForFunction(()=>window.aquarius);
    const entry=await page.evaluate(()=>window.aquarius.bundle.entry);
    await page.evaluate(({entry,frames})=>window.aquarius.run(entry,frames),{entry,frames:project==='painter'?1:2});
    const colors=await page.evaluate(async()=>{const pixels=await window.aquarius.host.processing.screen.target.readPixels(),colors=new Set();for(let i=0;i<pixels.length;i+=4)colors.add(`${pixels[i]},${pixels[i+1]},${pixels[i+2]}`);return colors.size;});
    assert.ok(colors>30,`${project} did not render a detailed GPU frame`);
    await page.screenshot({path:path.join(root,`${project}-browser.png`)});
    if(project==='marble') {
      const checked=await page.evaluate(()=>window.aquarius.run('smoke.aqua',0));assert.match(checked.output,/整合驗證通過/);
      await page.evaluate(()=>{window.gameTask=window.aquarius.run('main.aqua',0).catch(()=>{});});
      await page.waitForFunction(()=>window.aquarius.host.processing.frameCount>=3);
      await page.keyboard.press('p');
      await page.waitForFunction(async()=>{const h=window.aquarius.host,state=await h.runtime.invoke(h.cache.get('physics.aqua').scope.get('取得狀態快照'));return state.get('string:paused')[1];});
      await page.keyboard.press('p');await page.keyboard.down('w');
      await page.waitForFunction(async()=>{const h=window.aquarius.host,state=await h.runtime.invoke(h.cache.get('physics.aqua').scope.get('取得狀態快照'));return state.get('string:started')[1];});
      await page.keyboard.up('w');
      await page.setViewportSize({width:960,height:600});await page.waitForFunction(()=>window.aquarius.host.processing.screen.width===960);
    }
    await page.evaluate(()=>window.aquarius.stop());assert.equal(await page.locator('canvas,textarea').count(),0);assert.deepEqual(errors,[]);
    results.push({project,passed:true,colors});console.log(`PASS ${project}/browser-wasm (${colors} colors)`);
  }catch(error){results.push({project,passed:false,error:error.stack,errors});console.error(`FAIL ${project}: ${error.stack}`);}
  finally{await page.close();}
}}finally{await browser.close();await new Promise(r=>server.close(r));await writeFile(path.join(root,'browser-results.json'),JSON.stringify(results,null,2));}
if(results.some(r=>!r.passed))process.exitCode=1;
