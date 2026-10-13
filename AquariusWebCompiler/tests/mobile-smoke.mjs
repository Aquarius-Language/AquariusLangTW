import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const repository=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const root=path.resolve(process.env.AQUARIUS_MOBILE_PROJECT_ROOT??path.join(repository,'.android-build/projects'));
const output=path.join(repository,'.android-build/mobile-web');await mkdir(output,{recursive:true});
const defaults=['AquariusLang_MarbleRun_3D','AquariusLang_Snake','AquariusLang_Painter','AquariusLangTW_SingleUserKanban','AquariusLangTW_MediaPipeExamples','AquariusLang_PBR_ModelViewer','AquariusLangTW_AudioExamples'];
const names=process.env.AQUARIUS_MOBILE_CASES?.split(',')??defaults;
const mime={'.html':'text/html','.mjs':'text/javascript','.js':'text/javascript','.json':'application/json','.wasm':'application/wasm'};
const server=createServer(async(req,res)=>{try{const requested=decodeURIComponent(new URL(req.url,'http://localhost').pathname);const file=path.resolve(root,'.'+requested+(requested.endsWith('/')?'index.html':''));if(!file.startsWith(root+path.sep))throw new Error('Outside test projects');res.setHeader('Content-Type',mime[path.extname(file)]??'application/octet-stream');res.end(await readFile(file));}catch(e){res.statusCode=404;res.end(e.message);}});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const browser=await chromium.launch({channel:process.env.AQUARIUS_BROWSER??'chrome',headless:true,args:['--enable-unsafe-webgpu']});const results=[];
try{
  for(const name of names)for(const viewport of [{width:412,height:915},{width:915,height:412}]){
    const page=await browser.newPage({viewport,hasTouch:true,deviceScaleFactor:2}),errors=[];
    page.on('pageerror',e=>errors.push(e.message));page.on('console',message=>{if(message.type()==='error')errors.push(message.text());});
    try{
      const folder=name==='AquariusLangTW_SingleUserKanban'?'網頁':'web';
      await page.goto(`http://127.0.0.1:${server.address().port}/${name}/${folder}/?autorun=0`);
      await page.waitForFunction(()=>!!window.aquarius);
      const result=await page.evaluate(async()=>{
        let timer;try{return await Promise.race([window.aquarius.run(window.aquarius.bundle.entry,1),new Promise((_,reject)=>{timer=setTimeout(()=>{window.aquarius.host.runtime.cancelled=true;reject(new Error('Mobile sketch timed out'));},60000);})]);}finally{clearTimeout(timer);}
      });
      if(errors.length)throw new Error(errors.join('\n'));
      const pixels=await page.evaluate(async()=>{const screen=window.aquarius.host.processing.screen,bytes=await screen.target.readPixels();const colors=new Set();let visible=0;for(let i=0;i<bytes.length;i+=4){colors.add(`${bytes[i]},${bytes[i+1]},${bytes[i+2]}`);if(Math.abs(bytes[i]-bytes[0])+Math.abs(bytes[i+1]-bytes[1])+Math.abs(bytes[i+2]-bytes[2])>30)visible++;}return {width:screen.width,height:screen.height,colors:colors.size,visibleFraction:visible/(bytes.length/4)};});
      if(pixels.width!==viewport.width||pixels.height!==viewport.height||pixels.colors<8||pixels.visibleFraction<.01)throw new Error('Invalid mobile surface: '+JSON.stringify(pixels));
      await page.screenshot({path:path.join(output,`${name}-${viewport.width}.png`)});
      if(process.env.AQUARIUS_MOBILE_INTERACTION==='1'){
        await page.evaluate(()=>{window.mobileExecution=window.aquarius.run(window.aquarius.bundle.entry,0);window.mobileExecution.catch(error=>window.mobileFailure=error.stack);});
        await page.waitForFunction(()=>window.aquarius.host?.processing.frameCount>=1);
        const x=Math.round(viewport.width*.5),y=Math.round(viewport.height*.6);
        await page.touchscreen.tap(x,y);
        await page.waitForFunction(({x,y})=>{const p=window.aquarius.host?.processing,value=name=>{const v=p?.module.scope.get(name);return typeof v==='number'?v:v?.value;};return p&&Math.abs(value('mouseX')-x)<1&&Math.abs(value('mouseY')-y)<1&&p.eventQueue.length===0;},{x,y});
        if(name==='MultilingualInput'){
          await page.keyboard.insertText('星泉 👨‍👩‍👧‍👦');
          await page.waitForFunction(()=>window.aquarius.host.processing.module.scope.get('inputText')==='星泉 👨‍👩‍👧‍👦');
        }
        const failure=await page.evaluate(()=>window.mobileFailure);if(failure||errors.length)throw new Error(failure??errors.join('\n'));
        await page.evaluate(async()=>{window.aquarius.host.processing.exiting=true;await window.mobileExecution;});
      }
      results.push({name,viewport,passed:true,pixels,output:result.output});console.log(`PASS ${name} ${viewport.width}×${viewport.height}`);
    }catch(error){results.push({name,viewport,passed:false,error:error.stack});console.error(`FAIL ${name} ${viewport.width}×${viewport.height}: ${error.message}`);}
    finally{await page.close();}
  }
}finally{await browser.close();server.close();await writeFile(path.join(output,'results.json'),JSON.stringify(results,null,2));}
if(results.some(result=>!result.passed))process.exitCode=1;
