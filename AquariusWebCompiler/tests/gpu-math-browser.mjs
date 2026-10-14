// Real WebGPU smoke test. Export the fixture using the unified CLI first.
import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import {existsSync} from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
const repository=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const root=path.resolve(process.env.AQUARIUS_GPU_MATH_WEB??path.join(repository,'.web-build/gpu-math-browser'));
const artifacts=path.join(repository,'.web-build/gpu-math-report');await mkdir(artifacts,{recursive:true});
const server=createServer(async(req,res)=>{
  try{const request=decodeURIComponent(new URL(req.url,'http://localhost').pathname);const file=path.resolve(root,'.'+request+(request.endsWith('/')?'index.html':''));if(!file.startsWith(root+path.sep))throw new Error('Outside build');res.setHeader('Content-Type',({'.html':'text/html','.mjs':'text/javascript','.json':'application/json','.wasm':'application/wasm'})[path.extname(file)]??'application/octet-stream');res.end(await readFile(file));}catch{res.statusCode=404;res.end();}
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));let browser;
try{
  const executablePath=process.env.AQUARIUS_BROWSER_EXECUTABLE??['C:/Program Files/Google/Chrome/Application/chrome.exe','C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find(existsSync);
  browser=await chromium.launch({headless:true,args:['--enable-unsafe-webgpu'],...(executablePath?{executablePath}:{})});const page=await browser.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
  await page.waitForFunction(()=>document.getElementById('output').textContent.includes('[[5, 9, 15], [8, 12, 18], [1, 2, 3]]')||!document.getElementById('error').hidden,{},{timeout:60000});
  const error=await page.locator('#error').textContent();assert.equal(error,'');assert.deepEqual(errors,[]);
  const output=await page.locator('#output').textContent();assert.ok(output.includes('[[5, 9, 15], [8, 12, 18], [1, 2, 3]]'));
  const adapter=await page.evaluate(async()=>{const a=await navigator.gpu.requestAdapter();return a?.info??{};});
  await writeFile(path.join(artifacts,'result.json'),JSON.stringify({output,adapter},null,2));console.log('Aquarius Wasm GPUMath browser WebGPU passed');
}finally{await browser?.close();await new Promise(resolve=>server.close(resolve));}
