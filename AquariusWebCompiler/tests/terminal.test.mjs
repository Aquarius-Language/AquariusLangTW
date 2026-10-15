import test from 'node:test';
import assert from 'node:assert/strict';
import {registerTerminal} from '../../AquariusCore/hosting/terminal.mjs';
import {num} from '../../AquariusCore/hosting/values.mjs';
function host(){return {adapters:{platform:'web'},modules:new Map(),library(name){const m={};this.modules.set(name,m);return m;},newModule(){return {};},set(m,name,value){m[name]=value;},bind(m,library,name,fn){m[name]=fn;}};}
test('web exposes the terminal contract with explicit unsupported execution',async()=>{
 const h=host();registerTerminal(h);const m=h.modules.get('Terminal');assert.equal(m,h.modules.get('終端機'));assert.equal(m.IsSupported(),false);await assert.rejects(m.RunScript('echo test'),/unavailable/);
});
test('terminal validates arguments and options before invoking the Android adapter',async()=>{
 const h=host(),requests=[];registerTerminal(h,{platform:'android',isSupported:true,run:q=>{requests.push(q);return {exitCode:7,standardOutput:'中文',standardError:'diagnostic',timedOut:false};}});const m=h.modules.get('Terminal');
 await assert.rejects(m.Run('tool',[1]),/string/);assert.equal(requests.length,0);
 await assert.rejects(m.Run('tool',[],new Map([['timeout',['TimeoutMs',NaN]]])),/option/);assert.equal(requests.length,0);
 const r=await m.Run('tool',['a b',';literal'],new Map([['timeout',['TimeoutMs',num(1000,'int')]]]));assert.deepEqual(requests[0].args,['a b',';literal']);assert.equal(r.ExitCode.value,7);assert.equal(r.StandardOutput,'中文');assert.equal(requests[0].timeoutMs,1000);
 await m.RunScript('echo hello');assert.equal(requests[1].executable,'/system/bin/sh');assert.deepEqual(requests[1].args,['-c','echo hello']);
});
