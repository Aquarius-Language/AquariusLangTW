import test from 'node:test';
import assert from 'node:assert/strict';
import {Scope,VirtualMachine,num,inspect} from '../browser/vm.mjs';
import {uniformLayout} from '../browser/wgpu.mjs';
import {number,vector,mass,dimension} from '../browser/physics.mjs';
import {pack,unpack} from '../browser/processing.mjs';
for(const value of [NaN,Infinity,-Infinity,1000001,-1000001,'0',null])test(`physics rejects invalid scalar ${String(value)}`,()=>assert.throws(()=>number(value)));
for(const value of [-1,.0000001,.000999999,1000001])test(`physics rejects invalid mass ${value}`,()=>assert.throws(()=>mass(value)));
for(const value of [0,.00001,-1,10001])test(`physics rejects invalid dimension ${value}`,()=>assert.throws(()=>dimension(value)));
test('static mass and minimum dynamic mass remain distinct',()=>{assert.equal(mass(0),0);assert.equal(mass(.001),.001);});
test('physics vector validates every component and dimension',()=>{assert.deepEqual(vector([1,2,3]),[1,2,3]);assert.throws(()=>vector([1,2]));assert.throws(()=>vector([1,2,Infinity]));});
test('WGSL custom uniforms align vector and matrix fields including Chinese names',()=>{const fields=uniformLayout('struct UserUniforms { 值: f32, 對: vec2<f32>, 色: vec3<f32>, 模型: mat4x4<f32>, }');assert.deepEqual([...fields.values()].map(f=>f.offset),[0,8,16,32]);assert.equal(fields.get('模型').count,16);});
for(const source of ['struct UserUniforms { a: f32, a: f32, }','struct UserUniforms { a: array<f32>, }','struct UserUniforms { a: vec3<i32>, }'])test(`WGSL rejects unsupported layout ${source}`,()=>assert.throws(()=>uniformLayout(source)));
test('WGSL comments do not create fields',()=>assert.equal(uniformLayout('// struct UserUniforms { a: f32 }').size,0));
test('ARGB survives opaque and transparent pixel conversion',()=>{for(const c of [0,0xffffffff,0xff336699,0x11223344])assert.equal(pack(unpack(c)),c);});
test('scope assignments preserve captured ownership',()=>{const root=new Scope();root.create('counter',num(0,'int'));const inner=new Scope(root);inner.set('counter',num(42,'int'));assert.equal(root.get('counter').value,42);assert.equal(inner.get('counter').value,42);});
test('scope unknown reads and assignments fail',()=>{const scope=new Scope();assert.throws(()=>scope.get('missing'));assert.throws(()=>scope.set('missing',num(1)));});
test('VM lexical bindings shadow builtins even when their values are undefined or null',async()=>{
  const root=new Scope();root.create('shadow',num(42,'int'));
  const inner=new Scope(root),nested=new Scope(inner);
  const builtins=new Map([['shadow',num(99,'int')]]);
  const program={version:1,pool:[{type:'name',value:'shadow'}],code:[['Load',0]]};
  for(const value of [undefined,null,false,num(0,'int')]){
    inner.create('shadow',value);
    assert.equal(nested.get('shadow'),value);
    assert.equal(await new VirtualMachine({builtins}).execute(program,nested),value);
  }
});
test('VM falls back to present builtin values and reports only genuinely missing names',async()=>{
  const builtins=new Map([['undefinedBuiltin',undefined],['中文',num(7,'int')]]);
  const load=name=>({version:1,pool:[{type:'name',value:name}],code:[['Load',0]]});
  const vm=new VirtualMachine({builtins});
  assert.equal(await vm.execute(load('undefinedBuiltin')),undefined);
  assert.equal(inspect(await vm.execute(load('中文'))),'7');
  await assert.rejects(()=>vm.execute(load('missing')),{message:'Identifier not found: missing'});
});
test('builtin fallback retains asynchronous completion and propagates host errors',async()=>{
  const failure=new Error('host failure');
  const builtins=new Map([['wait',async()=>{await Promise.resolve();return num(42,'int');}],['fail',async()=>{throw failure;}]]);
  const call=name=>({version:1,pool:[{type:'name',value:name}],code:[['Load',0],['Call',0]]});
  const vm=new VirtualMachine({builtins});
  assert.equal(inspect(await vm.execute(call('wait'))),'42');
  await assert.rejects(()=>vm.execute(call('fail')),error=>error===failure);
});
test('bytecode version and unsupported opcodes fail explicitly',async()=>{const vm=new VirtualMachine({builtins:new Map()});await assert.rejects(()=>vm.execute({version:99}));await assert.rejects(()=>vm.execute({version:1,pool:[],code:[['Bogus',0]]}));});
test('VM cancellation stops before executing instructions',async()=>{const vm=new VirtualMachine({builtins:new Map()});vm.cancelled=true;await assert.rejects(()=>vm.execute({version:1,pool:[],code:[['Jump',0]]}),/cancelled/);});
test('results use Aquarius bilingual boolean and string formatting',()=>{assert.equal(inspect([true,false,'中文']), '[真, 假, 中文]');});
