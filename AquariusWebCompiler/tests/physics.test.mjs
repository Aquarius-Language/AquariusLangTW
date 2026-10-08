import test from 'node:test';
import assert from 'node:assert/strict';
import {BrowserJoltBackend} from '../browser/physics.mjs';
const near=(actual,expected,tolerance=.001)=>assert.ok(Math.abs(actual-expected)<tolerance,`${actual} differs from ${expected}`);
test('real Jolt WASM conserves impulse velocity, applies gravity, and solves floor contact',async()=>{
  const backend=new BrowserJoltBackend();try{
    const world=await backend.createWorld([0,0,0]),body=world.create('sphere',.5,[0,10,0],2);
    world.set('AddImpulse',body,[4,0,0]);for(let i=0;i<60;i++)world.step(1/60,1);
    near(world.get('GetPosition',body)[0],2);near(world.get('GetLinearVelocity',body)[0],2);
    world.setGravity([0,-9.81,0]);for(let i=0;i<60;i++)world.step(1/60,1);
    near(world.get('GetPosition',body)[1],5.01325);near(world.get('GetLinearVelocity',body)[1],-9.81);
    world.remove(body);world.create('box',[10,.5,10],[0,-.5,0],0);const ball=world.create('sphere',.5,[0,2,0],1);world.system.OptimizeBroadPhase();for(let i=0;i<240;i++)world.step(1/60,1);
    near(world.get('GetPosition',ball)[1],.5,.031);near(world.get('GetLinearVelocity',ball)[1],0,.01);
  }finally{backend.dispose();}
});
test('real Jolt WASM rejects foreign, removed, static and disposed resources',async()=>{
  const backend=new BrowserJoltBackend();try{
    const a=await backend.createWorld([0,0,0]),b=await backend.createWorld([0,0,0]),body=a.create('sphere',1,[0,0,0],1),floor=a.create('box',[1,1,1],[0,-5,0],0);
    assert.throws(()=>b.get('GetPosition',body),/live body/);assert.throws(()=>a.set('AddImpulse',floor,[1,0,0]),/dynamic/);
    a.remove(body);assert.throws(()=>a.get('GetPosition',body),/live body/);a.dispose();a.dispose();assert.throws(()=>a.getGravity(),/disposed/);
  }finally{backend.dispose();}
});
