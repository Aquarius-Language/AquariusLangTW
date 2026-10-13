import test from 'node:test';
import assert from 'node:assert/strict';
import {AndroidWindow} from '../template/app/src/main/assets/aquarius/android-window.mjs';
const fixture=events=>{const calls=[],host={input:{events}},bridge={now:()=>1000,title:value=>calls.push(value),resolveBack:value=>calls.push(value)};return {host,calls,window:new AndroidWindow(host,bridge)};};
test('Window capabilities use the cross-platform contract names',()=>{
  const f=fixture([]);for(const name of ['events','closeInterception','capture','touch','pen'])assert.equal(f.window.capabilities[name].available,true);
  for(const name of ['fileDrop','customCursor'])assert.equal(f.window.capabilities[name].available,false);
});
for(const [phase,type]of [['down','pointerPressed'],['move','pointerMoved'],['up','pointerReleased'],['cancel','inputCancelled']])test(`pointer ${phase} follows the shared event protocol`,()=>{
  const f=fixture([{type:'touch',phase,id:7,tool:'pen',eraser:true,x:10,y:20,pressure:.4,timestamp:2,history:[{x:9,y:19,pressure:.3,timestamp:1}]}]);
  const e=f.window.poll()[1];assert.equal(e.type,type);assert.equal(e.deviceId,7);assert.equal(e.eraser,true);assert.equal(e.contact,phase!=='up'&&phase!=='cancel');
  assert.equal(e.samples.length,2);assert.equal(e.samples[0].pressure,.3);assert.equal(e.samples[1].x,10);assert.equal(e.files,null);assert.equal(e.modifiers,0);
});
test('each Window consumes each frame once and owns its sample records',()=>{
  const event={type:'touch',phase:'move',id:1,tool:'touch',x:2,y:3,history:[{x:1,y:2}]},f=fixture([event]);
  const events=f.window.poll();events[1].samples[0].x=999;assert.equal(event.history[0].x,1);assert.deepEqual(f.window.poll(),[]);
  f.host.input={events:[]};assert.deepEqual(f.window.poll(),[]);
});
test('key names, repeats, modifiers and back negotiation remain portable',()=>{
  const f=fixture([{type:'keyPressed',code:67,key:'',modifiers:2},{type:'keyPressed',code:29,key:'a',repeat:true},{type:'back'}]);
  const events=f.window.poll();assert.equal(events[1].logicalKey,'Backspace');assert.equal(events[1].modifiers,2);assert.equal(events[2].type,'keyRepeat');assert.equal(events[3].type,'closeRequested');
  f.window.resolveClose('Cancel');assert.deepEqual(f.calls,['Cancel']);assert.throws(()=>f.window.resolveClose('Unknown'));
});
test('lifecycle states and destruction are observable',()=>{
  const f=fixture([{type:'visibility',state:false}]);assert.equal(f.window.poll()[1].state,false);f.window.dispose();assert.throws(()=>f.window.poll());assert.throws(()=>f.window.setTitle('Gone'));
});
