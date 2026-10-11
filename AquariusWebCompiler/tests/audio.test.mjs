import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {MiniaudioEngine,floatBits} from '../../AquariusCore/audio/audio-core.mjs';
const module=new WebAssembly.Module(readFileSync(new URL('../../AquariusCore/audio/audio.wasm',import.meta.url)));
const create=()=>new MiniaudioEngine(module);
test('desktop and AudioWorklet block sizes produce identical PCM',()=>{
  const render=block=>{const engine=create();try{
    const id=engine.tone(440,.3);engine.command(id,3,floatBits(.7));engine.command(id,4,floatBits(.3));engine.command(id,5,floatBits(1.25));engine.command(id,6,1);engine.command(id,0);
    const result=new Float32Array(8192);for(let frame=0;frame<4096;frame+=block)result.set(engine.render(block),frame*2);return Buffer.from(result.buffer);
  }finally{engine.dispose();}};
  assert.deepEqual(render(128),render(256));assert.deepEqual(render(128),render(4096));
});
test('audio is a memory-only Wasm library; silence, tone, pause, stop, seek and looping use the real engine',()=>{
  assert.deepEqual(WebAssembly.Module.imports(module),[]);
  const engine=create();try{
    assert.ok(engine.render(128).every(v=>v===0));
    const id=engine.tone(440,.05);assert.equal(engine.query(id,1),2400);
    engine.command(id,0);const pcm=engine.render(1024);assert.ok(pcm.some(v=>Math.abs(v)>.1));assert.ok(pcm.every(Number.isFinite));
    engine.command(id,1);assert.equal(engine.query(id,0),0);assert.ok(engine.render(128).every(v=>v===0));
    engine.command(id,2);assert.equal(engine.query(id,2),0);engine.command(id,6,1);engine.command(id,0);
    engine.render(4096);assert.equal(engine.query(id,0),1);
    engine.command(id,7,480);assert.equal(engine.query(id,2),480);
    engine.release(id);assert.throws(()=>engine.command(id,0),/live audio/);
  }finally{engine.dispose();engine.dispose();}assert.throws(()=>engine.render(1),/disposed/);
});
for(const format of ['flac','mp3'])test(`real ${format} decoder and resampler produce bounded stereo PCM`,()=>{
  const engine=create();try{
    const id=engine.decode(readFileSync(new URL(`./fixtures/audio/tone.${format}`,import.meta.url)));
    assert.ok(engine.query(id,1)>=4800);engine.command(id,0);
    const samples=engine.render(4096);assert.ok(samples.some(v=>Math.abs(v)>.02));assert.ok(samples.every(v=>Number.isFinite(v)&&Math.abs(v)<=1));
  }finally{engine.dispose();}
});
test('audio validates data, bounds voices and renders stereo pan, gain and decoded WAV',()=>{
  const engine=create();try{
    for(const n of [NaN,Infinity,0,20001])assert.throws(()=>engine.tone(n,1));
    assert.throws(()=>engine.buffer([1]));assert.throws(()=>engine.buffer([NaN,0]));assert.throws(()=>engine.render(4097));assert.throws(()=>engine.decode(new Uint8Array([1,2])));
    const id=engine.buffer(Array.from({length:512},()=>.5));engine.command(id,3,floatBits(.5));engine.command(id,4,floatBits(-1));engine.command(id,0);
    const pcm=engine.render(128);assert.ok(pcm.slice(2).filter((_,i)=>i%2===0).every(v=>Math.abs(v-.25)<.0001));assert.ok(pcm.filter((_,i)=>i%2===1).every(v=>v===0));engine.release(id);
    const wav=Buffer.alloc(44+480*2);wav.write('RIFF');wav.writeUInt32LE(wav.length-8,4);wav.write('WAVEfmt ',8);wav.writeUInt32LE(16,16);wav.writeUInt16LE(1,20);wav.writeUInt16LE(1,22);wav.writeUInt32LE(48000,24);wav.writeUInt32LE(96000,28);wav.writeUInt16LE(2,32);wav.writeUInt16LE(16,34);wav.write('data',36);wav.writeUInt32LE(960,40);for(let i=0;i<480;i++)wav.writeInt16LE(8192,44+i*2);
    const decoded=engine.decode(wav);assert.equal(engine.query(decoded,1),480);engine.command(decoded,0);assert.ok(engine.render(128).slice(2).every(v=>Math.abs(v-.25)<.0001));engine.release(decoded);
    const voices=Array.from({length:64},()=>engine.tone(440,.01));assert.throws(()=>engine.tone(440,.01));voices.forEach(id=>engine.release(id));assert.ok(engine.tone(440,.01)>0);
  }finally{engine.dispose();}
});
