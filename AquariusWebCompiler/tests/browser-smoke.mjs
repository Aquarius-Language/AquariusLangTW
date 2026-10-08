import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..'),build=path.join(root,'.web-build'),results=[];
const mime={'.html':'text/html','.mjs':'text/javascript','.js':'text/javascript','.json':'application/json','.wasm':'application/wasm'};
const server=createServer(async(req,res)=>{try{const requested=decodeURIComponent(new URL(req.url,'http://localhost').pathname),file=path.resolve(build,'.'+requested+(requested.endsWith('/')?'index.html':''));if(!file.startsWith(build+path.sep))throw new Error('Outside build');res.setHeader('Content-Type',mime[path.extname(file)]??'application/octet-stream');res.end(await readFile(file));}catch(e){res.statusCode=404;res.end(e.message);}});
await new Promise(r=>server.listen(0,'127.0.0.1',r));const port=server.address().port;
const browser=await chromium.launch({channel:process.env.AQUARIUS_BROWSER??'chrome',headless:true,args:['--enable-unsafe-webgpu']});
try{
  for(const project of (process.env.AQUARIUS_WEB_PROJECTS?.split(',')??['examples','marble','examples-bottle','marble-bottle','portable-bottle'])){
    const isMarble=project.startsWith('marble'),isExamples=project.startsWith('examples');
    const page=await browser.newPage({viewport:{width:1280,height:960}}),errors=[];page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});
    await page.goto(`http://127.0.0.1:${port}/${project}/`);try{await page.waitForFunction(()=>!!window.aquarius);}catch(e){throw new Error(`Browser initialization: ${errors.join('\n') || e.message}`);}
    let entries=await page.evaluate(()=>Object.keys(window.aquarius.bundle.modules));
    if(process.env.AQUARIUS_WEB_ENTRIES)entries=entries.filter(n=>process.env.AQUARIUS_WEB_ENTRIES.split(',').includes(n));
    for(const entry of entries){errors.length=0;const start=Date.now();try{
      const logical=entry.replace(/\.rius$/i,'.aqua');
      const frames=logical==='smoke.aqua'?0:logical==='color_mapping/main.aqua'?1:2;
      const r=await page.evaluate(async({entry,frames})=>{let timeout;try{return await Promise.race([window.aquarius.run(entry,frames).then(r=>({...r,ok:true})),new Promise((_,reject)=>{timeout=setTimeout(()=>{window.aquarius.host.vm.cancelled=true;reject(new Error(`Smoke test timed out at instruction ${window.aquarius.host.vm.instructions}, frame ${window.aquarius.host.processing.frameCount}`));},60000);})]);}catch(e){return {ok:false,error:e.stack,output:document.getElementById('output').textContent};}finally{clearTimeout(timeout);}},{entry,frames});
      if(entry.startsWith('generate_errors/')){if(r.ok||!r.error.includes('Identifier not found: array'))throw new Error('Expected undeclared array error');results.push({project,entry,passed:true,expectedFailure:true});console.log(`PASS expected error ${entry}`);continue;}
      if(!r.ok)throw new Error(r.error);if(errors.length)throw new Error(errors.join('\n'));
      if(logical==='smoke.aqua'&&!r.output.includes('整合驗證通過'))throw new Error('Marble gameplay assertions did not complete');
      if(['wgpu_compute/main.aqua','wgpu_triangle/main.aqua','bilingual_library/main.aqua'].includes(logical)&&r.result!=='真')throw new Error(`Unexpected result: ${r.result}`);
      if(['processing_wgpu/main.aqua','processing_showcase/main.aqua','color_mapping/main.aqua'].includes(logical)||(isMarble&&logical==='main.aqua')){
        const count=await page.evaluate(async()=>{const c=window.aquarius.host.processing.screen;const pixels=await c.target.readPixels();const colors=new Set();for(let i=0;i<pixels.length;i+=4)colors.add(`${pixels[i]},${pixels[i+1]},${pixels[i+2]}`);return colors.size;});if(count<8)throw new Error(`Insufficient rendered colors: ${count}`);
      }
      if(logical==='opengl_cube/main.aqua'&&!r.output.includes('OpenGL error: 0'))throw new Error('OpenGL error');
      if(isMarble&&(logical==='main.aqua'||logical==='smoke.aqua'))await page.screenshot({path:path.join(build,`${project}-${logical.replace('.aqua','')}.png`)});
      if(project==='portable-bottle'&&logical==='main.aqua'&&!r.result.startsWith('[40, 42, 封裝成功'))throw new Error(`Portable import/asset parity failed: ${r.result}`);
      results.push({project,entry,passed:true,ms:Date.now()-start});console.log(`PASS ${project}/${entry} (${Date.now()-start} ms)`);
    }catch(e){results.push({project,entry,passed:false,error:e.message,ms:Date.now()-start});console.log(`FAIL ${project}/${entry}: ${e.message}`);}}
    if(!process.env.AQUARIUS_WEB_ENTRIES){
      if(project==='portable-bottle'){
        await page.evaluate(async()=>{
          const h=window.aquarius.host,scope=h.processing.module.scope;
          const encode=s=>btoa(String.fromCharCode(...new TextEncoder().encode(s)));
          h.bundle.assets['assets/vertex.wgsl']=encode(h.bundle.graphics.vertex);
          h.bundle.assets['assets/fragment.wgsl']=encode(h.bundle.graphics.fragment);
          await scope.get('size')(16,16,'P2D');
          const custom=await scope.get('loadShader')('/ASSETS/fragment.wgsl','/assets/vertex.wgsl');
          await scope.get('shader')(custom);
          await scope.get('loadShader')('/assets/fragment.wgsl');
          const data=h.asset('/ASSETS/文字.txt');if(!new TextDecoder().decode(data).startsWith('封裝成功'))throw new Error('Portable text asset failed');
          await window.aquarius.stop();
        });
        results.push({project,entry:'packaged shader loading and Unicode assets',passed:true});console.log('PASS packaged shader loading and Unicode assets');
      }
      if(isExamples){
        await page.evaluate(()=>window.aquarius.run('multilingual_input/main.aqua',1));
        await page.evaluate(async()=>{document.querySelector('textarea').dispatchEvent(new InputEvent('input',{data:'e\u0301👨‍👩‍👧‍👦繁'}));await window.aquarius.host.processing.beginFrame();});
        await page.keyboard.press('Backspace');await page.evaluate(()=>window.aquarius.host.processing.beginFrame());
        const text=await page.evaluate(()=>window.aquarius.host.processing.events.get('textInput').env.get('內容'));
        if(text!=='e\u0301👨‍👩‍👧‍👦')throw new Error(`Grapheme input mismatch: ${text}`);
        results.push({project,entry:'DOM multilingual input and grapheme backspace',passed:true});console.log('PASS DOM multilingual input and grapheme backspace');
      }else if(isMarble){
        await page.evaluate(()=>{window.gameTask=window.aquarius.run('main.aqua',0).catch(()=>{});});
        await page.waitForFunction(()=>window.aquarius.host?.processing.frameCount>=3);
        await page.keyboard.press('p');
        await page.waitForFunction(async()=>{const host=window.aquarius.host,physics=host.cache.get('physics.aqua');const state=await host.vm.invoke(physics.scope.get('取得狀態快照'));return state.get('string:paused')[1]===true;});
        await page.keyboard.press('p');await page.keyboard.down('w');
        await page.waitForFunction(async()=>{const host=window.aquarius.host,physics=host.cache.get('physics.aqua');const state=await host.vm.invoke(physics.scope.get('取得狀態快照'));return state.get('string:started')[1]===true;});
        await page.keyboard.up('w');await page.evaluate(()=>window.aquarius.stop());
        results.push({project,entry:'DOM gameplay pause, movement, and Stop',passed:true});console.log('PASS DOM gameplay pause, movement, and Stop');
      }
    }
    await page.close();
  }
}finally{await browser.close();server.close();await writeFile(path.join(build,'browser-results.json'),JSON.stringify(results,null,2));}
if(results.some(r=>!r.passed))process.exitCode=1;
