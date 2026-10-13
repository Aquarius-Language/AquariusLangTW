import test from 'node:test';
import assert from 'node:assert/strict';
import {registerAndroidInput} from '../template/app/src/main/assets/aquarius/android-input.mjs';

function fixture(running = true) {
  const bindings = new Map(), calls = [];
  const input = {orientation:'portrait', keyboardVisible:true, insets:{left:0,top:24,right:0,bottom:16},events:[
    {type:'touch',id:7,phase:'move',x:12,y:33,pressure:.6,history:[{x:10,y:30,pressure:.5,timestamp:1}]}, {type:'back'},
  ]};
  const host = {modules:new Map(),processing:{running},input,record:fields=>({...fields}),library:name=>({name}),bind:(_m,_l,name,fn)=>bindings.set(name,fn)};
  const bridge = {poll:()=>JSON.stringify(input),keyboard:visible=>calls.push(['keyboard',visible]),vibrate:duration=>calls.push(['vibrate',duration]),resolveBack:decision=>calls.push(['back',decision])};
  registerAndroidInput(host,bridge);
  return {host,calls,call:(name,...args)=>bindings.get(name)(...args)};
}
test('Android input aliases identify the supported native module',()=>{
  const f=fixture();assert.equal(f.call('IsSupported'),true);assert.equal(f.host.modules.get('Android輸入').name,'AndroidInput');
});
test('frame polls retain all pointer ids, pressure and owned history without consuming events',()=>{
  const f=fixture(),first=f.call('Poll');assert.equal(first.backRequested,true);assert.equal(first.touches[0].id,7);assert.equal(first.touches[0].pressure,.6);
  first.touches[0].history[0].x=999;first.insets.top=999;
  const second=f.call('Poll');assert.equal(second.touches[0].history[0].x,10);assert.equal(second.insets.top,24);assert.equal(second.backRequested,true);
});
test('non-Processing clients receive native input snapshots',()=>{
  const f=fixture(false);f.host.input={events:[]};const poll=f.call('Poll');assert.equal(poll.orientation,'portrait');assert.equal(poll.keyboardVisible,true);
});
for(const value of [0,-1,10001,1.5,NaN,Infinity,'10',undefined])test(`invalid vibration ${String(value)} never reaches hardware`,()=>{
  const f=fixture();assert.throws(()=>f.call('Vibrate',value));assert.deepEqual(f.calls,[]);
});
for(const value of [1,10000])test(`bounded vibration ${value} reaches the Android service`,()=>{
  const f=fixture();f.call('Vibrate',value);assert.deepEqual(f.calls,[['vibrate',value]]);
});
for(const visible of [true,false])test(`keyboard visibility ${visible}`,()=>{
  const f=fixture();f.call('ShowKeyboard',visible);assert.deepEqual(f.calls,[['keyboard',visible]]);
});
test('keyboard requests require booleans',()=>{const f=fixture();assert.throws(()=>f.call('ShowKeyboard',1));assert.deepEqual(f.calls,[]);});
for(const decision of ['Accept','Cancel','Defer'])test(`back decision ${decision}`,()=>{
  const f=fixture();f.call('ResolveBack',decision);assert.deepEqual(f.calls,[['back',decision]]);
});
for(const decision of ['Exit','',null])test(`invalid back decision ${String(decision)}`,()=>{
  const f=fixture();assert.throws(()=>f.call('ResolveBack',decision));assert.deepEqual(f.calls,[]);
});
