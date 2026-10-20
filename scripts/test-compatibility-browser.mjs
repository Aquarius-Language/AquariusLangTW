// Run after test-extended-projects.ps1 and test-mineclone.ps1.
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {readFile, writeFile, mkdir} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {chromium} from '../AquariusWebCompiler/node_modules/playwright/index.mjs';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../.web-build/compatibility');
const selected=process.argv.slice(2);
let results=[];
if(selected.length){
  try{results=JSON.parse(await readFile(path.join(root,'browser-results.json'),'utf8')).filter(r=>!selected.includes(r.project));}catch{}
}
const server=createServer(async(req,res)=>{
  try{
    const pathname=decodeURIComponent(new URL(req.url,'http://localhost').pathname);
    const file=path.resolve(root,'.'+pathname+(pathname.endsWith('/')?'index.html':''));
    if(!file.startsWith(root+path.sep))throw Error('Outside compatibility output');
    res.setHeader('Content-Type',({'.html':'text/html','.mjs':'text/javascript','.json':'application/json','.wasm':'application/wasm'})[path.extname(file)]??'application/octet-stream');
    res.end(await readFile(file));
  }catch(error){res.statusCode=404;res.end(error.message);}
});
await new Promise(r=>server.listen(0,'127.0.0.1',r));
const browser=await chromium.launch({channel:process.env.AQUARIUS_BROWSER??'chrome',headless:true,args:['--enable-unsafe-webgpu',...(process.env.WEBGL_ANGLE?[`--use-angle=${process.env.WEBGL_ANGLE}`]:[])]});
try{
  for(const project of ['filament','mineclone']){
    if(selected.length&&!selected.includes(project))continue;
    const page=await browser.newPage({viewport:{width:960,height:600}}),errors=[];
    page.on('pageerror',e=>errors.push(e.message));
    page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});
    try{
      await page.addInitScript(()=>{
        globalThis.compatibilityContextLosses=0;
        document.addEventListener('webglcontextlost',event=>{
          if(event.target.parentElement?.id==='surfaces')globalThis.compatibilityContextLosses++;
        },true);
      });
      await page.goto(`http://127.0.0.1:${server.address().port}/${project}/web/?autorun=0`);
      await page.waitForFunction(()=>window.aquarius,null,{timeout:120000});
      let gpu;
      if(project==='filament'){
        await page.evaluate(()=>{window.compatibilityRun=window.aquarius.run('tests/capture_web.aqua').catch(e=>window.compatibilityError=e.message);});
        await page.waitForSelector('#surfaces canvas',{timeout:120000});
        gpu=await page.evaluate(()=>{
          const gl=document.querySelector('#surfaces canvas').getContext('webgl2');
          const debug=gl?.getExtension('WEBGL_debug_renderer_info');
          return debug?gl.getParameter(debug.UNMASKED_RENDERER_WEBGL):null;
        });
        console.log(project+' renderer: '+gpu);
      }else{
        await page.evaluate(()=>{window.compatibilityRun=window.aquarius.run('main.aqua').catch(e=>window.compatibilityError=e.message);});
      }
      await page.waitForFunction(()=>window.compatibilityError||['finished','error'].includes(window.aquarius.state),null,{timeout:300000});
      const result=await page.evaluate(()=>({state:window.aquarius.state,error:window.compatibilityError??document.querySelector('#error')?.textContent??'',output:document.querySelector('#output').textContent,shots:globalThis.__aquaFilamentScreenshots??[],contextLosses:globalThis.compatibilityContextLosses}));
      assert.equal(result.state,'finished',result.error);assert.equal(result.error,'');assert.deepEqual(errors,[]);
      if(project==='filament'){
        assert.equal(result.contextLosses,0);assert.match(result.output,/SHOWCASE_WEB_CAPTURE_OK/);
        assert.equal(result.shots.length,5);
        await mkdir(path.join(root,'filament/browser-captures'),{recursive:true});
        for(const shot of result.shots){
          const bytes=Buffer.from(shot.url.split(',')[1],'base64');
          assert.ok(bytes.length>10000,'Blank Filament scene: '+shot.path);
          await writeFile(path.join(root,'filament/browser-captures',path.basename(shot.path)),bytes);
        }
      }else{
        // The debug script exercises world edits, crafting, chests, the furnace and saving.
        const output=await page.locator('#output').textContent();
        assert.match(output,/savecheck inventory match (?:真|true)/);assert.match(output,/debug step quit/);
        result.output=output;
      }
      results.push({project,passed:true,state:result.state,captures:result.shots.length,output:result.output,renderer:gpu,contextLosses:result.contextLosses});
      console.log(`PASS ${project}/browser`);
    }catch(error){
      const output=await page.locator('#output').textContent().catch(()=>null);
      results.push({project,passed:false,error:error.stack,errors,output});console.error(`FAIL ${project}: ${error.stack}\n${output}`);
    }
    finally{await page.close();}
  }
}finally{
  await browser.close();server.closeAllConnections();await new Promise(r=>server.close(r));
  await writeFile(path.join(root,'browser-results.json'),JSON.stringify(results,null,2));
}
if(results.some(r=>!r.passed))process.exitCode=1;
