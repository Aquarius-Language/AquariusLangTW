const capability = available => ({available,restriction:null});
const fields = type => ({type,timestamp:0,logicalKey:null,physicalKey:null,modifiers:0,deviceId:null,device:null,button:null,wheelX:null,wheelY:null,wheelUnit:null,samples:null,contact:null,eraser:null,files:null,state:null});
const specialKeys = {19:'ArrowUp',20:'ArrowDown',21:'ArrowLeft',22:'ArrowRight',66:'Enter',67:'Backspace',111:'Escape',61:'Tab',112:'Delete'};

/** Adapts Android snapshots to the existing Window event protocol. */
export class AndroidWindow {
  constructor(host,bridge){this.host=host;this.bridge=bridge;this.disposed=false;this.pending=[{...fields('created'),timestamp:bridge.now()/1000}];}
  get capabilities(){return {events:capability(true),fileDrop:capability(false),closeInterception:capability(true),customCursor:capability(false),capture:capability(true),touch:capability(true),pen:capability(true)};}
  live(){if(this.disposed)throw new Error('Window has been destroyed');}
  setTitle(title){this.live();if(typeof title!=='string')throw new Error('Expected title');this.bridge.title(title);}
  setCursor(){this.live();}
  setCustomCursor(){throw new Error('Unsupported: Android custom cursor');}
  capturePointer(id){this.live();if(!Number.isInteger(id)||id<0)throw new Error('Invalid pointer id');}
  releasePointer(id){this.capturePointer(id);}
  resolveClose(decision){this.live();if(!['Accept','Cancel','Defer'].includes(decision))throw new Error('Invalid close decision');this.bridge.resolveBack(decision);}
  poll(){
    this.live();const input=this.host.input;if(this.previous===input)return [];this.previous=input;
    const events=this.pending.splice(0);
    for(const event of input.events){
      let result={...fields(event.type),timestamp:event.timestamp??this.bridge.now()/1000};
      if(event.type==='touch'){
        result={...result,type:{down:'pointerPressed',up:'pointerReleased',move:'pointerMoved',cancel:'inputCancelled'}[event.phase],deviceId:event.id,device:event.tool,button:0,contact:event.phase!=='up'&&event.phase!=='cancel',eraser:event.eraser??false,
          samples:[...event.history,event].map(sample=>({x:sample.x,y:sample.y,pressure:sample.pressure,timestamp:sample.timestamp,tiltX:null,tiltY:null}))};
      }else if(event.type==='back')result.type='closeRequested';
      else if(event.type==='keyPressed'||event.type==='keyReleased')Object.assign(result,{type:event.repeat?'keyRepeat':event.type,logicalKey:event.key||specialKeys[event.code]||null,physicalKey:event.physicalKey??null,modifiers:event.modifiers??0});
      else if(event.type==='textInput')Object.assign(result,{type:'text',text:event.text});
      else if(event.type.startsWith('composition'))Object.assign(result,{text:event.text,state:event.type!=='compositionEnded'});
      else Object.assign(result,{state:event.state??null});
      events.push(result);
    }
    return events;
  }
  dispose(){this.disposed=true;this.pending=[];}
}
