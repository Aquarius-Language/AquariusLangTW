import {registerAndroidInput} from './android-input.mjs';
import {CapabilityHost} from './capability-host.mjs';
import {AndroidWgpuBackend} from './android-gpu.mjs';
import {frameDelay} from './frame-timing.mjs';
import {JoltWorld,vector} from './physics-core.mjs';
import initJolt from './vendor-jolt.mjs';
import {num,inspect,unwrap} from './values.mjs';
import {resizeSurfaceSize} from './surface-size.mjs';
import {lookupPath} from './package-paths.mjs';
import {registerOnnx,validateRun} from './onnx-registration.mjs';
import {registerMediaPipe} from './mediapipe.mjs';
import {registerApplication} from './android-application.mjs';
import {registerAudio} from './audio-registration.mjs';
import {MiniaudioEngine} from './audio-core.mjs';

class AndroidJoltBackend {
  constructor(){this.name='Jolt Physics';this.worlds=new Set();}
  async createWorld(gravity){vector(gravity);this.initialized??=initJolt({wasmBinary:decode64(native.readAsset('vendor-jolt.wasm'))});const world=new JoltWorld(await this.initialized,gravity);this.worlds.add(world);return world;}
  dispose(){for(const w of this.worlds)w.dispose();this.worlds.clear();}
}
class AndroidOnnxBackend {
  constructor(){this.sessions=new Set();this.disposed=false;}
  isSupported(provider){return !this.disposed&&provider==='CPU';}
  async createSession(model,provider,signal){signal?.throwIfAborted();if(!this.isSupported(provider))throw new Error('Unsupported: Android ONNX provider '+provider);const descriptor=JSON.parse(native.inference('create',JSON.stringify(model.asset?{asset:model.asset}:{model:encode64(model)}))),session={...descriptor,provider,disposed:false,live(){if(this.disposed)throw new Error('ONNX session disposed');},async run(inputs,outputs,signal){this.live();signal?.throwIfAborted();validateRun(this.inputs,this.outputs,inputs,outputs);const feeds=Object.fromEntries([...inputs].map(([name,t])=>[name,{type:t.type,shape:t.shape,data:encode64(t.data)}]));const result=JSON.parse(native.inference('run',JSON.stringify({id:this.id,inputs:feeds,outputs})));const types={float32:Float32Array,float64:Float64Array,int32:Int32Array,uint8:Uint8Array,bool:Uint8Array};return new Map(Object.entries(result).map(([name,t])=>[name,{...t,data:new types[t.type](decode64(t.data).buffer)}]));},async dispose(){if(this.disposed)return;native.inference('dispose',JSON.stringify({id:this.id}));this.disposed=true;}};this.sessions.add(session);return session;}
  async dispose(){this.disposed=true;for(const s of this.sessions)await s.dispose();this.sessions.clear();}
}
class AndroidAudioBackend {
  constructor(host){this.host=host;this.devices=new Set();this.disposed=false;}
  async createDevice(offline){
    if(this.disposed)throw new Error('Audio backend disposed');
    const engine=new MiniaudioEngine(await WebAssembly.compile(decode64(native.readAsset('audio.wasm'))));
    const device={engine,offline,disposed:false,output:offline?0:native.audio('create',0,''),running:false,last:performance.now(),
      live(){if(this.disposed)throw new Error('Audio device disposed');},
      pump(){if(!this.running)return;const now=performance.now();let frames=Math.max(0,Math.min(8192,Math.round((now-this.last)*48)));this.last=now;while(frames>0){const n=Math.min(frames,4096);native.audio('write',this.output,encode64(this.engine.renderView(n)));frames-=n;}},
      call(operation,...args){this.live();this.pump();return this.engine[operation](...args);},
      render(frames){if(!this.offline)throw new Error('Render requires an offline device');return Array.from(this.engine.render(frames));},
      async start(){this.live();if(this.offline)throw new Error('Offline devices have no output');native.audio('start',this.output,'');this.running=true;this.last=performance.now()-40;this.pump();},
      async suspend(){this.live();if(!this.offline){this.running=false;native.audio('suspend',this.output,'');}},
      async dispose(){if(this.disposed)return;this.running=false;if(!this.offline)native.audio('dispose',this.output,'');this.engine.dispose();this.disposed=true;}};
    this.devices.add(device);return device;
  }
  pump(){for(const d of this.devices)d.pump();}
  async dispose(){this.disposed=true;for(const d of this.devices)await d.dispose();this.devices.clear();}
}
function registerCamera(host){const m=host.library('Camera');host.modules.set('相機',m);const sessions=new Set(),call=(op,args={})=>JSON.parse(native.camera(op,JSON.stringify(args)));host.bind(m,'Camera','IsSupported',()=>true);host.bind(m,'Camera','Enumerate',()=>call('enumerate').devices.map(d=>host.record({識別碼:d.id,名稱:d.name,方向:d.facing})));host.bind(m,'Camera','Open',(id,width,height,fps)=>{if(typeof id!=='string'||![width,height,fps].every(Number.isInteger)||width<1||width>1920||height<1||height>1080||fps<1||fps>60)throw new Error('InvalidArgument: Invalid camera options');const session=call('open',{id,width,height,fps}),s=host.newModule();sessions.add(session.id);host.bind(s,'Camera.Session','IsOpen',()=>sessions.has(session.id)&&call('isOpen',session).open);host.bind(s,'Camera.Session','Dispose',()=>{if(sessions.delete(session.id))call('dispose',session);});host.bind(s,'Camera.Session','Capture',()=>{if(!sessions.has(session.id))throw new Error('Closed: Camera session closed');const frame=call('capture',session),pixels=decode64(frame.pixels),r=host.record({寬度:num(frame.width,'int'),高度:num(frame.height,'int'),序號:frame.sequence,時間戳記:frame.timestamp,方向:frame.facing,影像:host.application.image({width:frame.width,height:frame.height,pixels,alpha:"Opaque"})});host.bind(r,'Camera.Frame','GetPixels',()=>Array.from(pixels,n=>num(n,'int')));return r;});return s;});host.camera={dispose(){for(const id of sessions)call('dispose',{id});sessions.clear();}};
  const webcam=host.library('攝影機');host.modules.set('Webcam',webcam);host.modules.set('網路攝影機',webcam);host.bind(webcam,'攝影機','是否支援',()=>false);host.bind(webcam,'攝影機','列出裝置',()=>[]);host.bind(webcam,'攝影機','開啟',()=>{throw new Error('Unsupported: Use 相機 on Android');});
}
export class AndroidHost extends CapabilityHost {
  constructor(bundle,options){super(bundle,{...options,adapters:{platform:'android',graphics:abi=>new AndroidWgpuBackend(abi),physics:()=>new AndroidJoltBackend(),register:host=>{registerApplication(host);registerAudio(host,new AndroidAudioBackend(host));registerOnnx(host,new AndroidOnnxBackend());registerMediaPipe(host);registerCamera(host);registerAndroidInput(host,native);}}});this.input={events:[],insets:{},orientation:'portrait',keyboardVisible:false};}
  record(fields){const r=this.newModule();for(const [name,value]of Object.entries(fields))this.set(r,name,value);return r;}
  modelAsset(path){return {asset:this.bundle.assets[lookupPath(this.bundle.assets,this.resolve(path))]};}
  asset(path){const raw=native.readAssetBytes(this.modelAsset(path).asset);return new Uint8Array(raw.buffer,raw.byteOffset,raw.byteLength);}
  installProcessingInput(p){p.textarea={style:{},focus:()=>native.keyboard(true),blur:()=>native.keyboard(false),remove(){}};}
  screenSize(){return JSON.parse(native.viewport());}
  surface(width,height,title){const host=this,canvas={style:{},width:0,height:0},surface={canvas,size:{width,height,pixelWidth:width,pixelHeight:height},requestSize(){},readSize(){const size=host.screenSize();return resizeSurfaceSize(this.size,size.width,size.height,size.pixelWidth,size.pixelHeight);},apply(size){this.size=size;canvas.width=size.pixelWidth;canvas.height=size.pixelHeight;},dispose(){}};surface.apply(surface.readSize());return surface;}
  measureText(text,font,size){return {width:native.measureText(text,font,size)};}
  rasterText(text,style,density){const r=JSON.parse(native.raster(JSON.stringify({text,font:style.font,size:style.textSize,leading:style.leading,color:style.fill,density})));r.bytes=decode64(r.bytes);return r;}
  // Return to the Java actor between slices. V8 can then release WeakRef leases,
  // run GC and observe cancellation before the next timer resolves.
  async yieldExecution(){this.audio.pump();await new Promise(resolve=>setTimeout(resolve,1));native.waitFrame(1);}
  async nextFrame(fps=60,start=performance.now()){this.audio.pump();await new Promise(resolve=>setTimeout(resolve,0));const now=performance.now();native.awaitFrame(now+frameDelay(fps,start,now));this.input=JSON.parse(native.poll());const p=this.processing;for(const e of this.input.events){if(e.type==='touch'){if(e.phase==='down'&&this.primaryPointer==null)this.primaryPointer=e.id;if(e.id!==this.primaryPointer)continue;if(e.phase==='up'||e.phase==='cancel')this.primaryPointer=null;const fields={pmouseX:p.module.scope.get('mouseX'),pmouseY:p.module.scope.get('mouseY'),mouseX:e.x,mouseY:e.y,mouseButton:num(0,'int'),isMousePressed:e.phase==='down'||e.phase==='move'};p.eventQueue.push({name:e.phase==='down'?'mousePressed':e.phase==='up'||e.phase==='cancel'?'mouseReleased':'mouseDragged',fields});if(e.phase==='up')p.eventQueue.push({name:'mouseClicked',fields});}else if(e.type==='textInput'){p.eventQueue.push({name:'textInput',fields:{inputText:e.text,inputTimestamp:e.timestamp}});for(const char of e.text)p.eventQueue.push({name:'keyTyped',fields:{key:char}});}
      else if(e.type==='inputCancelled'){p.keys.clear();this.primaryPointer=null;p.eventQueue.push({name:'mouseReleased',fields:{isMousePressed:false,isKeyPressed:false}});}else if(e.type.startsWith('composition')){p.composing=e.type!=='compositionEnded';p.eventQueue.push({name:e.type,fields:{compositionText:e.text,compositionCursor:num(e.text.length,'int'),isComposing:p.composing}});}
      else if(e.type==='keyPressed'||e.type==='keyReleased'){const map={19:265,20:264,21:263,22:262,66:257,67:259,111:256,61:258,112:261,59:340,60:344,113:341,114:345,57:342,58:346},code=map[e.code]??(e.code>=131&&e.code<=142?e.code+159:e.key?e.key.toUpperCase().charCodeAt(0):-1);if(e.type==='keyPressed')p.keys.add(code);else p.keys.delete(code);p.eventQueue.push({name:e.type,fields:{keyCode:num(code,'int'),key:e.key,keyTimestamp:e.timestamp,isKeyPressed:p.keys.size>0}});} else if(e.type==='back'){if(!p.events.has('closeRequested')&&!this.application.windows.size&&!this.androidInputObserved){p.exiting=true;native.resolveBack('Accept');}else p.eventQueue.push({name:'closeRequested',fields:{}});}}}
  async imageBytes(path){const image=await this.application.images.decode(this.asset(path),0,this.signal);return {width:image.width,height:image.height,bytes:image.pixels};}
  async saveImage(path,width,height,pixels){const result=JSON.parse(native.image('encode',JSON.stringify({width,height,pixels:encode64(pixels),format:'Png'})));await this.download(path,new Blob([decode64(result.bytes)]));}
  async download(name,blob){const bytes=encode64(new Uint8Array(await blob.arrayBuffer()));if(this.frameLimit){native.storage('put','download/'+name,JSON.stringify({bytes}));return;}const files=JSON.parse(native.picker('save',JSON.stringify({suggestedName:name.split('/').at(-1)}))).files;if(files.length)native.document('write',JSON.stringify({uri:files[0].uri,bytes}));}
}
export async function run(frames=0){const bundle=JSON.parse(native.readJson());bundle.compiledModule=await WebAssembly.compile(decode64(native.readAsset('program.wasm')));const host=new AndroidHost(bundle,{frameLimit:frames,print:message=>native.log(message)});globalThis.aquarius=host;try{const result=await host.execute(bundle.entry);native.log(inspect(result));}finally{await host.dispose();}}
