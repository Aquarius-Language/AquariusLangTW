import {audioFormat,audioNumber,floatBits} from './audio-core.mjs';
import {num} from './values.mjs';
export function registerAudio(host,backend){
  host.audio=backend;const module=host.library('Audio');host.modules.set('音訊',module);
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
