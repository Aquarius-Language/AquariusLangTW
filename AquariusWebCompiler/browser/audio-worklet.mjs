import {MiniaudioEngine} from './audio-core.mjs';

// Commands and rendering share the audio thread. No platform decoder or mixer participates.
class AquariusAudioProcessor extends AudioWorkletProcessor {
  constructor(options){
    super();this.active=false;this.failed=false;
    try{this.engine=new MiniaudioEngine(options.processorOptions.module);this.port.postMessage({ready:true});}
    catch(e){this.failed=true;this.port.postMessage({error:e.message});}
    this.port.onmessage=({data:{id,operation,args}})=>{
      try{
        if(this.failed)throw new Error('Audio processor is unavailable');
        let result;
        if(operation==='start')this.active=true;
        else if(operation==='suspend')this.active=false;
        else if(operation==='dispose'){this.active=false;this.engine.dispose();}
        else if(!['tone','buffer','decode','command','query','release'].includes(operation))throw new Error('Unknown audio operation');
        else result=this.engine[operation](...args);
        this.port.postMessage({id,result});
      }catch(e){this.port.postMessage({id,error:e.message});}
    };
  }
  process(inputs,outputs){
    const channels=outputs[0];if(!channels?.length||!this.active||this.failed)return true;
    try{
      const pcm=this.engine.renderView(channels[0].length);
      for(let i=0;i<channels[0].length;i++){channels[0][i]=pcm[i*2];channels[1][i]=pcm[i*2+1];}
    }catch(e){this.failed=true;this.port.postMessage({error:e.message});}
    return true;
  }
}
registerProcessor('aquarius-miniaudio',AquariusAudioProcessor);
