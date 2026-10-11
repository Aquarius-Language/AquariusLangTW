// miniaudio library ABI 1. Used by offline tests and the realtime AudioWorklet.
export const audioFormat=Object.freeze({sampleRate:48000,channels:2,maxSounds:64,maxFrames:48000*180,maxEncodedBytes:32*1024*1024});
const scalarBuffer=new ArrayBuffer(4),scalarFloat=new Float32Array(scalarBuffer),scalarInt=new Int32Array(scalarBuffer);
export function floatBits(value){scalarFloat[0]=value;return scalarInt[0];}
export function audioNumber(value,min,max){if(typeof value!=='number'||!Number.isFinite(value)||value<min||value>max)throw new Error(`Expected a finite number in ${min}..${max}`);return Math.fround(value);}
export class MiniaudioEngine {
  constructor(compiledModule){
    if(WebAssembly.Module.imports(compiledModule).length)throw new Error('Audio library must have no host imports');
    this.e=new WebAssembly.Instance(compiledModule,{}).exports;this.disposed=false;this.sounds=new Set();
    this.e._initialize();
    if(this.e.audio_abi()!==1)throw new Error('Unsupported audio library ABI');this.check(this.e.audio_init());
  }
  live(){if(this.disposed)throw new Error('Audio device has been disposed');}
  check(value){if(value<0)throw new Error(`miniaudio operation failed (${value})`);return value;}
  own(id){this.check(id);this.sounds.add(id);return id;}
  sound(id){this.live();if(!this.sounds.has(id))throw new Error('Expected a live audio sound from this device');}
  upload(bytes,operation,count){this.live();const pointer=this.e.audio_alloc(bytes.byteLength);if(!pointer)throw new Error('Audio memory limit exceeded');try{new Uint8Array(this.e.memory.buffer,pointer,bytes.byteLength).set(bytes);return this.own(this.e[operation](pointer,count));}finally{this.e.audio_free(pointer);}}
  tone(frequency,seconds){this.live();return this.own(this.e.audio_tone(floatBits(audioNumber(frequency,20,20000)),Math.round(audioNumber(seconds,.001,60)*48000)));}
  buffer(samples){this.live();if(!samples||samples.length<2||samples.length%2||samples.length>audioFormat.maxFrames*2)throw new Error('Expected interleaved stereo samples');const data=Float32Array.from(samples,v=>audioNumber(v,-1,1));return this.upload(new Uint8Array(data.buffer),'audio_buffer',data.length/2);}
  decode(bytes){this.live();if(!bytes?.byteLength||bytes.byteLength>audioFormat.maxEncodedBytes)throw new Error('Encoded audio must be 1..32 MiB');return this.upload(bytes,'audio_decode',bytes.byteLength);}
  command(id,command,value=0){this.sound(id);this.check(this.e.audio_command(id,command,value));}
  query(id,query){this.sound(id);return this.check(this.e.audio_query(id,query));}
  release(id){this.sound(id);this.check(this.e.audio_release(id));this.sounds.delete(id);}
  renderView(frames){this.live();if(!Number.isInteger(frames)||frames<1||frames>4096)throw new Error('Render expects 1..4096 stereo frames');const pointer=this.e.audio_render(frames);if(!pointer)throw new Error('Audio render failed');return new Float32Array(this.e.memory.buffer,pointer,frames*2);}
  render(frames){return this.renderView(frames).slice();}
  dispose(){if(this.disposed)return;this.e.audio_dispose();this.sounds.clear();this.disposed=true;}
}
