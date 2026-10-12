import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFile,writeFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
const repository=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const root=path.resolve(process.env.AQUARIUS_PBR_WEB??path.join(repository,'.web-build/pbr/showcase/web')),report=path.join(repository,'.web-build/pbr');
// A nested URL reproduces the relative asset URLs used by GitHub project Pages.
const server=createServer(async(req,res)=>{try{const requested=decodeURIComponent(new URL(req.url,'http://localhost').pathname);if(!requested.startsWith('/project/'))throw Error('Bad prefix');const file=path.resolve(root,'.'+requested.slice(8)+(requested.endsWith('/')?'index.html':''));if(!file.startsWith(root+path.sep))throw Error('Outside build');res.setHeader('Content-Type',({'.html':'text/html','.mjs':'text/javascript','.json':'application/json','.wasm':'application/wasm'})[path.extname(file)]??'application/octet-stream');res.end(await readFile(file));}catch(error){res.statusCode=404;res.end(error.message);}});
await new Promise(r=>server.listen(0,'127.0.0.1',r));const browser=await chromium.launch({channel:process.env.AQUARIUS_BROWSER??'chrome',headless:true,args:['--enable-unsafe-webgpu']}),errors=[];
try {
  const page=await browser.newPage({viewport:{width:1300,height:950}});page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});
  await page.goto(`http://127.0.0.1:${server.address().port}/project/?autorun=0`);await page.waitForFunction(()=>window.aquarius);
  const smoke=await page.evaluate(()=>window.aquarius.run('smoke.aqua',0));assert.match(smoke.output,/整合驗證通過/);
  const gpu=await page.evaluate(()=>window.aquarius.run('gpu-smoke.aqua',0));assert.match(gpu.output,/GPU 驗證通過/);
  await page.evaluate(()=>window.aquarius.run('main.aqua',3));
  const colors=await page.evaluate(async()=>{const pixels=await window.aquarius.host.processing.screen.target.readPixels(),colors=new Set();for(let i=0;i<pixels.length;i+=4)colors.add(`${pixels[i]},${pixels[i+1]},${pixels[i+2]}`);return colors.size;});assert.ok(colors>500,`Insufficient rendered detail: ${colors}`);
  await page.screenshot({path:path.join(report,'browser.png')});
  await page.evaluate(()=>{window.pbrTask=window.aquarius.run('main.aqua',0).catch(e=>{if(!e.message.includes('cancel'))throw e;});});
  await page.waitForFunction(()=>window.aquarius.host.processing.frameCount>=3);await page.keyboard.press('Space');
  await page.waitForFunction(()=>document.getElementById('output').textContent.includes('模型旋轉： 假'));
  await page.setViewportSize({width:960,height:700});await page.waitForFunction(()=>window.aquarius.host.processing.screen.width===960);
  await page.evaluate(()=>window.aquarius.stop());assert.equal(await page.locator('canvas,textarea').count(),0);assert.deepEqual(errors,[]);
  await writeFile(path.join(report,'browser-results.json'),JSON.stringify({passed:true,colors,smoke:smoke.output,gpu:gpu.output},null,2));console.log(`PASS PBR / model import / GPU pixels / resize / cleanup (${colors} colors)`);
} finally {await browser.close();await new Promise(r=>server.close(r));}
