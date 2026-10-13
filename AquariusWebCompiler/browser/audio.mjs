import {MiniaudioEngine,audioFormat,audioNumber,floatBits} from './audio-core.mjs';
import {num} from './values.mjs';

export class BrowserAudioBackend {
  constructor(host){this.host=host;this.devices=new Set();this.disposed=false;this.module=null;}
  async compiled(){
    this.module??=fetch(new URL('./audio.wasm',import.meta.url)).then(async response=>{if(!response.ok)throw new Error(`Could not load audio (${response.status})`);return WebAssembly.compile(await response.arrayBuffer());});
    return this.module;
  }
  async createDevice(offline){
    if(this.disposed)throw new Error('Audio backend has been disposed');
    const compiled=await this.compiled();if(this.disposed)throw new Error('Audio backend has been disposed');
    const device=await BrowserAudioDevice.create(compiled,offline,this.host.signal);
    if(this.disposed){await device.dispose();throw new Error('Audio backend has been disposed');}
    this.devices.add(device);return device;
  }
  async dispose(){if(this.disposed)return;this.disposed=true;await Promise.allSettled([...this.devices].map(d=>d.dispose()));this.devices.clear();}
}

export class BrowserAudioDevice {
  constructor(offline,signal){this.offline=offline;this.signal=signal;this.disposed=false;this.pending=new Map();this.nextId=0;}
  static async create(compiled,offline,signal){
    const device=new BrowserAudioDevice(offline,signal);
    if(offline){device.engine=new MiniaudioEngine(compiled);return device;}
    if(!globalThis.AudioContext||!globalThis.AudioWorkletNode)throw new Error('Audio output requires AudioWorklet in a secure context');
    device.context=new AudioContext({sampleRate:audioFormat.sampleRate,latencyHint:'interactive'});
    try{
      if(device.context.sampleRate!==audioFormat.sampleRate)throw new Error('Audio output must support 48000 Hz');
      await device.context.audioWorklet.addModule(new URL('./audio-worklet.mjs',import.meta.url));
      device.node=new AudioWorkletNode(device.context,'aquarius-miniaudio',{numberOfInputs:0,numberOfOutputs:1,outputChannelCount:[2],processorOptions:{module:compiled}});
      await new Promise((resolve,reject)=>{
        device.node.port.onmessage=({data})=>{
          if(data.ready){resolve();return;}
          if(data.id){const pending=device.pending.get(data.id);if(pending){device.pending.delete(data.id);data.error?pending.reject(new Error(data.error)):pending.resolve(data.result);}return;}
          if(data.error){device.failure=new Error(data.error);reject(device.failure);device.rejectPending(device.failure);}
        };
        device.node.onprocessorerror=()=>{device.failure=new Error('Audio processor failed');reject(device.failure);device.rejectPending(device.failure);};
      });
      device.node.connect(device.context.destination);return device;
    }catch(e){await device.dispose();throw e;}
  }
  live(){if(this.disposed)throw new Error('Audio device has been disposed');if(this.failure)throw this.failure;if(this.signal?.aborted)throw new Error('Execution cancelled');}
  rejectPending(error){for(const pending of this.pending.values())pending.reject(error);this.pending.clear();}
  call(operation,...args){
    this.live();if(this.offline)return this.engine[operation](...args);
    return new Promise((resolve,reject)=>{const id=++this.nextId;this.pending.set(id,{resolve,reject});this.node.port.postMessage({id,operation,args});});
  }
  async start(){
    this.live();if(this.offline)throw new Error('Offline devices have no output. Use Render.');
    // Only audio applications request a gesture; existing applications get no additional UI.
    if(this.context.state!=='running'){
      const resume=this.context.resume();
      if(this.context.state!=='running')await this.unlock(resume);
      else await resume;
    }
    await this.call('start');
  }
  unlock(resume){
    return new Promise((resolve,reject)=>{
      const button=document.createElement('button');button.textContent='開啟音訊';button.style.cssText='position:fixed;inset:45% auto auto 45%;z-index:10000;padding:16px 28px;font-size:20px';
      const finish=error=>{button.remove();this.signal?.removeEventListener('abort',abort);this.cancelUnlock=null;error?reject(error):resolve();};
      const abort=()=>finish(new Error('Execution cancelled'));
      this.cancelUnlock=abort;this.signal?.addEventListener('abort',abort,{once:true});
      button.onclick=()=>this.context.resume().then(()=>finish(),finish);
      document.body.append(button);resume.then(()=>{if(button.isConnected)finish();},finish);
      if(this.signal?.aborted)abort();
    });
  }
  async suspend(){this.live();if(this.offline)return;await this.call('suspend');await this.context.suspend();}
  render(frames){this.live();if(!this.offline)throw new Error('Render requires an offline device');return Array.from(this.engine.render(frames));}
  async dispose(){
    if(this.disposed)return;this.cancelUnlock?.();this.disposed=true;this.rejectPending(new Error('Audio device has been disposed'));
    if(this.offline){this.engine.dispose();return;}
    this.node?.disconnect();this.node?.port.close();if(this.context?.state!=='closed')await this.context?.close();
  }
}

import {registerAudio as registerPortableAudio} from './audio-registration.mjs';
export function registerAudio(host,backend=new BrowserAudioBackend(host)){return registerPortableAudio(host,backend);}
