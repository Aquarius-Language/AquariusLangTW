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

export function registerAudio(host){
  host.audio=new BrowserAudioBackend(host);const module=host.library('Audio');host.modules.set('音訊',module);
  host.set(module,'SampleRate',num(audioFormat.sampleRate,'int'));host.set(module,'取樣率',num(audioFormat.sampleRate,'int'));host.set(module,'Channels',num(2,'int'));host.set(module,'聲道數',num(2,'int'));
  const ownSound=(device,id)=>{
    const sound=host.newModule();let released=false;
    const live=()=>{device.live();if(released)throw new Error('Audio sound has been disposed');};
    const bind=(name,fn)=>host.bind(sound,'Audio.Sound',name,(...args)=>{live();return fn(...args);});
    for(const [name,command]of [['Play',0],['Pause',1],['Stop',2]])bind(name,async()=>{await device.call('command',id,command,0);});
    for(const [name,command,min,max]of [['SetVolume',3,0,1],['SetPan',4,-1,1],['SetPitch',5,.25,4]])bind(name,async n=>{await device.call('command',id,command,floatBits(audioNumber(n,min,max)));});
    bind('SetLooping',async value=>{if(typeof value!=='boolean')throw new Error('Expected a boolean');await device.call('command',id,6,value?1:0);});
    bind('Seek',async seconds=>{const duration=await device.call('query',id,1);await device.call('command',id,7,Math.round(audioNumber(seconds,0,duration/48000)*48000));});
    bind('IsPlaying',async()=>Boolean(await device.call('query',id,0)));
    bind('GetDuration',async()=>await device.call('query',id,1)/48000);
    bind('GetPosition',async()=>await device.call('query',id,2)/48000);
    host.bind(sound,'Audio.Sound','Dispose',async()=>{if(released)return;released=true;if(!device.disposed)await device.call('release',id);});return sound;
  };
  const create=async offline=>{
    const device=await host.audio.createDevice(offline),module=host.newModule();
    const bind=(name,fn)=>host.bind(module,'Audio.Device',name,(...args)=>{device.live();return fn(...args);});
    bind('Start',()=>device.start());bind('Suspend',()=>device.suspend());
    bind('CreateTone',async(frequency,seconds)=>ownSound(device,await device.call('tone',frequency,seconds)));
    bind('CreateBuffer',async samples=>{if(!Array.isArray(samples))throw new Error('Expected interleaved stereo samples');return ownSound(device,await device.call('buffer',samples));});
    bind('Load',async path=>{if(typeof path!=='string')throw new Error('Expected an audio asset path');return ownSound(device,await device.call('decode',host.asset(path)));});
    bind('Render',frames=>device.render(frames));host.bind(module,'Audio.Device','Dispose',async()=>{host.audio.devices.delete(device);await device.dispose();});return module;
  };
  host.bind(module,'Audio','CreateDevice',()=>create(false));host.bind(module,'Audio','CreateOfflineDevice',()=>create(true));
}
