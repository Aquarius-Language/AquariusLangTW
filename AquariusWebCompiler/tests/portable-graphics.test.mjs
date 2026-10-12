import test from 'node:test';
import assert from 'node:assert/strict';
import {compile} from './wasm-fixture.mjs';
import {Scope,module,unwrap,portableBuiltin} from '../browser/values.mjs';
import {WasmRuntime} from '../browser/wasm.mjs';
import {uniformLayout} from '../browser/wgpu.mjs';

async function run(source,extra=new Map()) {
  const bundle=compile({'main.aqua':source}),builtins=new Map(extra),host={bundle,builtins},vm=new WasmRuntime(host);
  for(const [name,id] of [['長度',1],['加入',4]]){const fn=()=>{throw Error('Portable algorithm called the host');};fn[portableBuiltin]=id;builtins.set(name,fn);}
  builtins.set('匯入',async name=>{const path=name==='模型'?'.aquarius/Models.aqua':`.aquarius/${name}.aqua`;assert.ok(path in bundle.modules);const scope=new Scope();await vm.execute(bundle.modules[path],scope,builtins);return module(scope);});
  await vm.ready();return await vm.execute(bundle.modules['main.aqua']);
}
const field=(r,key)=>r.get('string:'+key)[1];
const triangle='v 0 0 0\nv 2 0 0\nv 0 2 0\nf 1 2 3\n';
const parse=(text,format='OBJ')=>run(`變數 m=匯入("Models");m.Parse${format}("${text}");`);
test('core model algorithms run in browser Wasm without an algorithm provider',async()=>{
  const r=await parse(triangle);assert.equal(field(r,'ok'),true);assert.equal(unwrap(field(r,'triangleCount')),1);
  assert.deepEqual(unwrap(field(r,'vertices')),[[0,0,0,0,0,1,0,0],[2,0,0,0,0,1,0,0],[0,2,0,0,0,1,0,0]]);
});
test('UTF-16 indexing uses the same guest runtime in browsers',async()=>assert.deepEqual(unwrap(await run('變數 s="星泉";[s[0],s[1],s[-1],s[2]];')),['星','泉',null,null]));
test('OBJ comments, BOM, scientific numbers, normals, UVs and negative references survive import',async()=>{
  const r=await parse('\ufeff# comment\r\no 星泉\r\nv 0 0 0\r\nv +2e0 0 0\r\nv 0 2 0\r\nvt 0 0\nvt 1 0\nvt 0 1\nvn 0 0 2\nusemtl 金屬\nf -3/1/1 -2/2/1 -1/3/1');
  assert.equal(field(r,'ok'),true);assert.deepEqual(unwrap(field(r,'vertices'))[1],[2,0,0,0,0,1,1,0]);assert.equal(field(field(r,'groups')[0],'material'),'金屬');
});
test('concave polygons preserve signed area and winding',async()=>{
  for(const reverse of [false,true]){const r=await parse('v 0 0 0\nv 2 0 0\nv 2 2 0\nv 1 1 0\nv 0 2 0\n'+(reverse?'f 5 4 3 2 1':'f 1 2 3 4 5'));assert.equal(field(r,'ok'),true);
    const v=unwrap(field(r,'vertices'));let area=0;for(let i=0;i<v.length;i+=3){const [a,b,c]=v.slice(i,i+3),cross=(b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]);assert.ok(reverse?cross<0:cross>0);area+=cross/2;}assert.equal(area,reverse?-3:3);
  }
});
test('bad and unsupported geometry returns a bounded diagnostic',async()=>{
  for(const text of ['',triangle.replace('1 2 3','0 2 3'),triangle.replace('1 2 3','-4 2 3'),triangle.replace('1 2 3','1//1 2 3'),triangle.replace('1 2 3','1/ 2 3'),triangle.replace('2 0 0','NaN 0 0'),triangle.replace('2 0 0','1e99 0 0'),'vn 0 0 0','curv 1 2 3','o '+'x'.repeat(257),triangle+'f '+'1 '.repeat(257)]){
    const r=await parse(text);assert.equal(field(r,'ok'),false,text);assert.equal(typeof field(r,'error'),'string');
  }
});
test('MTL metallic/roughness and legacy shininess match desktop results',async()=>{
  const r=await parse('newmtl Copper\nKd .8 .4 .1\nPm 1\nNs 98','MTL');assert.equal(field(r,'ok'),true);const m=field(r,'materials')[0];assert.equal(unwrap(field(m,'metallic')),1);assert.ok(Math.abs(unwrap(field(m,'roughness'))-Math.sqrt(.02))<1e-12);
  for(const text of ['newmtl a\nKd 2 0 0','newmtl a\nmap_Kd x.png','newmtl a\nd .5','newmtl a\nnewmtl a'])assert.equal(field(await parse(text,'MTL'),'ok'),false);
});
test('PBR defaults, validation and shader ABI use compiled bindings',async()=>{
  const writes=new Map(),scope=new Scope();scope.create('set',(name,value)=>{writes.set(name,unwrap(value));});const shader=module(scope);
  const r=await run('變數 p=匯入("PBR");變數 material=p.Material([0.5d,0.2d,0.1d],1.0d,0.0d);[p.SetMaterial(shader,material),p.SetLight(shader,[0,0,1],[4,4,4]),p.SetLight(shader,[0,0,0],[1,1,1]),p.SetEnvironment(shader,material,0.1d,2.0d,[0,0,0]),p.FragmentShader];',new Map([['shader',shader]]));
  assert.deepEqual(r.slice(0,4),[true,true,false,true]);assert.deepEqual(writes.get('parameters'),[1,0,2,.1]);assert.equal(uniformLayout(r[4]).size,5);
  for(const args of ['[1,1,1],-1,0','[2,0,0],0,0','[1,1,1],0.0d/0.0d,0','[1,1,1],0,1.1d'])assert.equal(field(await run(`變數 p=匯入("PBR");p.Material(${args});`),'ok'),false);
});
