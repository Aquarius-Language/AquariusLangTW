import {readFileSync} from 'node:fs';
import {MiniaudioEngine,floatBits} from '../../AquariusCore/audio/audio-core.mjs';
const engine=new MiniaudioEngine(new WebAssembly.Module(readFileSync(new URL('../../AquariusCore/audio/audio.wasm',import.meta.url))));
try{
  const tone=engine.tone(440,.1),buffer=engine.buffer(Array.from({length:512},(_,i)=>i%2?.1:-.1));
  engine.command(tone,3,floatBits(.7));engine.command(tone,4,floatBits(.35));engine.command(tone,5,floatBits(1.25));engine.command(tone,6,1);engine.command(tone,0);
  engine.command(buffer,6,1);engine.command(buffer,0);const samples=engine.render(4096);
  const blocks=[Buffer.from(samples.buffer)];
  engine.command(tone,2);engine.command(buffer,2);
  for(const format of ['flac','mp3']){
    const id=engine.decode(readFileSync(new URL(`./fixtures/audio/tone.${format}`,import.meta.url)));
    engine.command(id,0);blocks.push(Buffer.from(engine.render(4096).buffer));engine.release(id);
  }
  console.log(Buffer.concat(blocks).toString('base64'));
}finally{engine.dispose();}
