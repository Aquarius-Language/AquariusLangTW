import {chromium} from 'playwright';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
const output=path.resolve(process.argv[2]??'.web-build/network');
const browser=await chromium.launch({channel:process.env.AQUARIUS_BROWSER??'chrome',headless:true,args:['--enable-unsafe-webgpu']});
const report={};
try{
  const page=await browser.newPage({viewport:{width:560,height:820}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto('http://127.0.0.1:8787/?autorun=0');await page.waitForFunction(()=>window.aquarius);
  report.verification=await page.evaluate(()=>window.aquarius.run('驗證.aqua',0));assert.equal(report.verification.result,'真');assert.match(report.verification.output,/網路驗證完成 真/);
  report.protocols=await page.evaluate(()=>window.aquarius.run('協定驗證.aqua',0));assert.equal(report.protocols.result,'真');assert.match(report.protocols.output,/網路協定驗證完成 真/);
  report.ui=await page.evaluate(()=>window.aquarius.run(window.aquarius.bundle.entry,100));assert.match(report.ui.output,/網路往返成功/);assert.ok((report.ui.output.match(/網路往返成功/g)??[]).length>=2);
  report.capabilities=await page.evaluate(()=>[...window.aquarius.host.network.features]);assert.equal(report.capabilities.includes('tcp'),false);assert.equal(report.capabilities.includes('http'),true);
  report.colors=await page.evaluate(async()=>{const bytes=await window.aquarius.host.processing.screen.target.readPixels(),colors=new Set();for(let i=0;i<bytes.length;i+=4)colors.add(bytes[i]+','+bytes[i+1]+','+bytes[i+2]);return colors.size;});assert.ok(report.colors>30);
  await page.screenshot({path:path.join(output,'web.png')});assert.deepEqual(errors,[]);report.errors=errors;
  await fs.writeFile(path.join(output,'browser-report.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report,null,2));
}finally{await browser.close();}
