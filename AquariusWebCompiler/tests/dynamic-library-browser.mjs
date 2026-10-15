import {chromium} from 'playwright';
import {createServer} from 'node:http';
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const website=path.resolve(process.argv[2]??path.join(root,'examples/dynamic_linking/dist/web'));
const mime={'.html':'text/html','.mjs':'text/javascript','.js':'text/javascript','.json':'application/json','.wasm':'application/wasm'};
const server=createServer(async(req,res)=>{
  try {
    const relative=decodeURIComponent(new URL(req.url,'http://localhost').pathname), file=path.resolve(website,'.'+relative+(relative.endsWith('/')?'index.html':''));
    if(!file.startsWith(website+path.sep))throw new Error('Outside website');
    res.setHeader('Content-Type',mime[path.extname(file)]??'application/octet-stream');res.end(await readFile(file));
  }catch{res.statusCode=404;res.end('Not found');}
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const browser=await chromium.launch({channel:process.env.AQUARIUS_BROWSER??'chrome',headless:true});
try {
  const page=await browser.newPage(), errors=[];page.on('pageerror',error=>errors.push(error.message));
  await page.goto(`http://127.0.0.1:${server.address().port}/?autorun=0`);
  await page.waitForFunction(()=>window.aquarius?.state==='ready');
  const result=await page.evaluate(()=>window.aquarius.run());
  assert.match(result.output,/DYNAMIC_LINKING_OK/);assert.match(result.output,/10 \+ 20 \+ 12 = 42/);
  assert.match(result.output,/JavaScript: JS & WebAssembly/);assert.match(result.output,/繁體中文 UTF-8: 你好，星泉！/);
  // Use actual exported modules to check aliases, binary transport and invalid calls.
  const checks=await page.evaluate(async()=>{
    const h=window.aquarius.host,m=h.modules.get('DynamicLibrary'),load=m.scope.get('Load');
    const lib=await load('libraries/portable/showcase.wasm');
    const bytes=await lib.scope.get('Invoke')('echo',[{type:'int',value:0},{type:'int',value:255}]);
    let failures=0;
    for(const [symbol,input]of [['failure',''],['oversized',''],['missing',''],['invalid_utf8','']])try{await lib.scope.get('Call')(symbol,input);}catch{failures++;}
    await lib.scope.get('Dispose')();await lib.scope.get('釋放')();
    try{await lib.scope.get('HasSymbol')('echo');}catch{failures++;}
    return {aliases:m===h.modules.get('動態函式庫')&&load===m.scope.get('載入'),bytes:bytes.map(v=>v.value),failures};
  });
  assert.deepEqual(checks,{aliases:true,bytes:[0,255],failures:5});assert.deepEqual(errors,[]);
  await page.evaluate(()=>window.aquarius.stop());
  const report={platform:'web',passed:true,checks,output:result.output};
  await mkdir(path.join(root,'.web-build/dynamic-linking'),{recursive:true});
  await writeFile(path.join(root,'.web-build/dynamic-linking/browser.json'),JSON.stringify(report,null,2));
  console.log(JSON.stringify(report,null,2));
}finally{await browser.close();await new Promise(resolve=>server.close(resolve));}
