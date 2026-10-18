import {textureDescriptor,decodeTextureDataAsync} from './wgpu-textures.mjs';
import {registerArrayLibrary} from './array-library.mjs';
import {registerHashLibrary} from './hash-library.mjs';
import {registerStringLibrary} from './string-library.mjs';
import {Scope,module,num,numeric,wrap,unwrap,inspect,nativeCall,nativeOwner,isPromiseLike,portableBuiltin} from './values.mjs';
import {WasmRuntime} from './wasm-host.mjs';
import {Processing,pack} from './processing.mjs';
import {mat4,vec3} from './vendor-gl-matrix.mjs';
import {registerTerminal} from './terminal.mjs';
const vector=v=>{if(!Array.isArray(v)||v.length!==3||v.some(n=>!Number.isFinite(n)))throw Error('Expected a finite three-element vector');return v;};
import {resolvePath,lookupPath} from './package-paths.mjs';
export class CapabilityHost {
  constructor(bundle,{signal=new AbortController().signal,frameLimit=0,print=console.log,adapters}={}) {
    if(bundle.version!==2)throw new Error('Unsupported application version');this.adapters=adapters;this.bundle=bundle;this.signal=signal;this.frameLimit=frameLimit;this.print=print;this.modules=new Map();this.objects=new Map();this.cache=new Map();this.importing=new Set();this.downloads=new Map();this.graphics=adapters.graphics(bundle.graphics);this.builtins=new Map();this.runtime=new WasmRuntime(this);this.register();
  }
  newModule(){return module(new Scope());}
  set(m,n,v){m.scope.create(n,wrap(v));if(m===this.processing?.module && (n==="width"||n==="height"))m.scope.create(n==="width"?"寬度":"高度",wrap(v));}
  bind(m,library,name,fn,raw=false){const aliases={PVector:'Processing.Vector',PImage:'Processing.Image',PShape:'Processing.Shape',PShader:'Processing.Shader'},lib=aliases[library]??library;const item=this.bundle.catalog.find(f=>f.library===lib&&f.english===name)??this.bundle.catalog.find(f=>f.english===name);const entry=(...a)=>{if(this.signal.aborted)throw new Error('Execution cancelled');if(item&&(a.length<item.min||a.length>item.max))throw new Error(library.startsWith("攝影機")?`攝影機.${name}：參數無效：需要 ${item.min} 個參數，實際收到 ${a.length} 個。`:`${library}.${name}: Expected ${item.min}..${item.max} arguments, got ${a.length}`);const result=fn(...(raw?a:a.map(unwrap)));return isPromiseLike(result)?Promise.resolve(result).then(value=>raw?value:wrap(value)):raw?result:wrap(result);};const callable=async(...a)=>entry(...a);callable[nativeCall]=entry;callable[nativeOwner]=m;m.scope.create(name,callable);if(item?.chinese)m.scope.create(item.chinese,callable);return callable;}
  library(name){const m=this.newModule();this.modules.set(name,m);return m;}
  builtinsFor(path){const map=new Map(this.builtins),cwd='/'+path.split('/').slice(0,-1).join('/');map.set('目前工作目錄',cwd);map.set('currWorkingDir',cwd);const fn=this.globalImport(path);map.set('import',fn);map.set('匯入',fn);return map;}
  globalImport(path){return async name=>{if(typeof name!=='string')throw new Error('import expects a string');if(this.modules.has(name))return this.modules.get(name);const full=lookupPath(this.bundle.modules,(this.bundle.portableLibraries?.[name]??this.resolve(name,path)),{module:true});if(this.importing.has(full))throw new Error(`Circular import: ${full}`);this.importing.add(full);try{const env=new Scope();await this.runtime.execute(this.bundle.modules[full],env,this.builtinsFor(full));const m=module(env);this.cache.set(full,m);this.cache.set(full.replace(/\.aqua$/i,'.aqua'),m);return m;}finally{this.importing.delete(full);}};}
  resolve(name,path=''){return resolvePath(name,path);}
  async execute(entry){entry=lookupPath(this.bundle.modules,this.resolve(entry),{module:true});if(this.importing.has(entry))throw new Error(`Circular import: ${entry}`);this.importing.add(entry);try{return await this.runtime.execute(this.bundle.modules[entry],new Scope(),this.builtinsFor(entry));}finally{this.importing.delete(entry);this.webcam?.closeSessions();}}
  registerGlobals(){const globals=this.newModule(),b=(n,f,raw=false)=>this.bind(globals,'',n,f,raw);
    b('currentTime',()=>num(performance.now(),'double'),true);b('currentDate',()=>{const d=new Date();return d.getFullYear()+'-'+String(d.getMonth()+1).padStart(2,'0')+'-'+String(d.getDate()).padStart(2,'0');});
    b('len',v=>num(typeof v==='string'?v.length:Array.isArray(v)?v.length:v instanceof Map?v.size:(()=>{throw new Error('len expects string, array or hash');})(),'int'),true);b('last',v=>{if(!Array.isArray(v))throw new Error('last expects array');return v.at(-1)??null;},true);b('rest',v=>{if(!Array.isArray(v))throw new Error('rest expects array');return v.length?v.slice(1):null;},true);b('push',(a,v)=>{if(!Array.isArray(a))throw new Error('push expects array');return [...a,v];},true);b('print',(...a)=>{this.print(a.map(inspect).join(' '));},true);for(const n of ['isOSWindows','isOSLinux','isOSMacOS'])b(n,()=>false);b('isOSAndroid',()=>this.adapters.platform==='android');b('isWeb',()=>this.adapters.platform==='web');b('execFile',()=>{throw new Error('execFile requires a desktop host');});this.builtins=new Map(globals.scope.store);
    b('floodFill',()=>{throw new Error('floodFill requires compiled Wasm execution');},true);this.builtins=new Map(globals.scope.store);
    for(const [name,id]of [['len',1],['last',2],['rest',3],['push',4],['floodFill',5]])this.builtins.get(name)[portableBuiltin]=id;
  }
  register(){this.registerGlobals();registerArrayLibrary(this);registerHashLibrary(this);registerStringLibrary(this);
    this.processing=new Processing(this);this.modules.set('Processing',this.processing.module);this.registerGpu();registerTerminal(this);this.registerMath();this.registerText();this.adapters.registerLegacy?.(this);
    this.adapters.register(this);this.modules.set("繪圖",this.processing.module);if(this.modules.has("Serialization"))this.modules.set("序列化",this.modules.get("Serialization"));
    for(const [library,alias]of [['Camera','相機'],['AndroidInput','Android輸入']])if(!this.modules.has(library)){
      const m=this.library(library);this.modules.set(alias,m);this.bind(m,library,'IsSupported',()=>false);
      for(const name of library==='Camera'?['Enumerate','Open']:['Poll','ShowKeyboard','Vibrate','ResolveBack'])this.bind(m,library,name,()=>{if(name==='Enumerate')return [];throw new Error('Unsupported: '+library+' is unavailable on '+this.adapters.platform);});
    }
  }
  registerGpu(){const m=this.library('WGPU');this.modules.set('wgpu',m);this.set(m,'Backend',this.graphics.name??'webgpu');
    const floats=(values,what='wgpu data')=>{if(!Array.isArray(values)||!values.length||values.length>16*1024*1024||values.some(v=>typeof v!=='number'||!Number.isFinite(v)||!Number.isFinite(Math.fround(v))))throw new Error(`${what} needs 1..16777216 finite floats`);return values;};
    const intArg=(v,name)=>{if(!Number.isInteger(v))throw new Error(`${name} must be an integer`);return v;};
    this.bind(m,'WGPU','CreateWindow',(w,h,title)=>{
      if(typeof this.graphics.createWindow!=='function')throw new Error(`WGPU windows are unavailable on ${this.adapters.platform} / 此平台不支援 wgpu 視窗`);
      if(![w,h].every(n=>Number.isInteger(n)&&n>=1&&n<=8192)||typeof title!=='string')throw new Error('CreateWindow expects width/height 1..8192 and a title');
      const win=this.graphics.createWindow(w,h,title),wm=this.newModule();this.objects.set(wm,win);
      const publish=()=>{const s=win.state;this.set(wm,'Width',num(s.width,'int'));this.set(wm,'Height',num(s.height,'int'));this.set(wm,'PixelWidth',num(s.pixelWidth,'int'));this.set(wm,'PixelHeight',num(s.pixelHeight,'int'));
        this.set(wm,'MouseX',num(s.mouseX,'double'));this.set(wm,'MouseY',num(s.mouseY,'double'));this.set(wm,'MouseDeltaX',num(s.mouseDeltaX,'double'));this.set(wm,'MouseDeltaY',num(s.mouseDeltaY,'double'));this.set(wm,'Wheel',num(s.wheel,'double'));
        this.set(wm,'Focused',s.focused);this.set(wm,'PointerLocked',s.pointerLocked);this.set(wm,'ShouldClose',s.shouldClose);this.set(wm,'Resized',s.resized);};
      const b=(n,f)=>this.bind(wm,'WGPU.Window',n,(...a)=>{win.live();return f(...a);});publish();
      b('Poll',async()=>{await win.poll();publish();});
      b('KeyDown',k=>win.keyDown(intArg(k,'Key')));b('KeyPressed',k=>win.keyPressed(intArg(k,'Key')));b('KeyReleased',k=>win.keyReleased(intArg(k,'Key')));
      b('MouseDown',k=>win.mouseDown(intArg(k,'Button')));b('MousePressed',k=>win.mousePressed(intArg(k,'Button')));b('MouseReleased',k=>win.mouseReleased(intArg(k,'Button')));
      b('SetPointerLock',v=>{if(typeof v!=='boolean')throw new Error('SetPointerLock expects a Boolean');win.setPointerLock(v);publish();});
      b('SetTitle',t=>{if(typeof t!=='string')throw new Error('SetTitle expects a string');win.setTitle(t);});b('Close',()=>{win.close();publish();});
      this.bind(wm,'WGPU.Window','Dispose',()=>win.dispose());return wm;
    });
    this.bind(m,'WGPU','CreateDevice',async(windowModule)=>{
    let d;if(windowModule===undefined)d=await this.graphics.createDevice();else{const win=this.objects.get(windowModule);if(!win||typeof this.graphics.createDeviceForWindow!=='function')throw new Error('CreateDevice expects a live WGPU window');d=await this.graphics.createDeviceForWindow(win);}
    const dm=this.newModule();this.objects.set(dm,d);this.set(dm,'Backend',d.backend);const b=(n,f)=>this.bind(dm,'WGPU.Device',n,f);b('Dispose',()=>d.dispose());b('Poll',()=>d.poll());
    b('Present',target=>{const t=this.objects.get(target);if(!t||typeof d.present!=='function')throw new Error('Expected a render target from this wgpu device');return d.present(t);});
    b('CreateVertexBuffer',values=>{floats(values,'Vertex buffer');if(values.length%12)throw new Error('Vertex buffers need a multiple of 12 floats per vertex (position, normal, UV, RGBA)');d.live();if(typeof d.createVertexBuffer!=='function')throw new Error('Retained vertex buffers are unavailable on this platform');
      const buffer=d.createVertexBuffer(values),vm=this.newModule();this.objects.set(vm,buffer);this.set(vm,'Length',num(buffer.length,'int'));
      this.bind(vm,'WGPU.VertexBuffer','Write',v=>buffer.write(floats(v)));this.bind(vm,'WGPU.VertexBuffer','WriteRange',(offset,v)=>buffer.writeRange(intArg(offset,'Offset'),floats(v)));this.bind(vm,'WGPU.VertexBuffer','Dispose',()=>buffer.dispose());return vm;});
    b('CreateBuffer',values=>{if(Number.isInteger(values)){if(values<1||values>16*1024*1024)throw new Error('Buffer length must be 1..16777216 floats');}else floats(values);d.live();const buffer=d.createBuffer(values),bm=this.newModule();this.objects.set(bm,buffer);this.set(bm,'Length',num(buffer.length,'int'));this.bind(bm,'WGPU.Buffer','Write',v=>buffer.write(v));this.bind(bm,'WGPU.Buffer','Read',()=>buffer.read());this.bind(bm,'WGPU.Buffer','WriteRange',(offset,v)=>buffer.writeRange(offset,v));this.bind(bm,'WGPU.Buffer','ReadRange',(offset,count)=>buffer.readRange(offset,count));this.bind(bm,'WGPU.Buffer','Dispose',()=>buffer.dispose());return bm;});
    b('Dispatch',(source,buffer,groups)=>{if(!Number.isInteger(groups)||groups<1||groups>65535)throw new Error('Workgroup count must be 1..65535');const obj=this.objects.get(buffer);if(!obj||obj.owner!==d)throw new Error('Expected a live buffer from this wgpu device');return d.dispatch(source,obj,groups);});
    b('LoadTexture',async(path,w,h,format,dimension,levels)=>{const descriptor=textureDescriptor(w,h,format,dimension,levels),texture=await this.loadTexture(d,descriptor,path),m=this.newModule();this.objects.set(m,texture);this.bind(m,'WGPU.Texture','Dispose',()=>texture.dispose());return m;});
    b('CreateShader',async(v,f)=>{const shader=await d.createShader(v,f),sm=this.newModule();this.objects.set(sm,shader);this.bind(sm,'WGPU.Shader','Set',(n,v)=>shader.set(n,v));this.bind(sm,'WGPU.Shader','SetInt',(n,v)=>shader.set(n,[v],true));this.bind(sm,'WGPU.Shader','SetTexture',(n,m)=>shader.setTexture(n,this.objects.get(m)));this.bind(sm,'WGPU.Shader','SetBuffer',(n,m)=>{if(typeof shader.setBuffer!=='function')throw new Error('Shader storage buffers are unavailable on this platform');shader.setBuffer(n,this.objects.get(m));});this.bind(sm,'WGPU.Shader','Dispose',()=>shader.dispose());return sm;});
    b('CreateRenderTarget',(w,h,format='rgba8unorm')=>{const target=d.createTarget(w,h,format),tm=this.newModule();this.objects.set(tm,target);this.set(tm,'Width',num(w,'int'));this.set(tm,'Height',num(h,'int'));this.bind(tm,'WGPU.Target','GetTexture',()=>{const texture=target.getTexture(),m=this.newModule();this.objects.set(m,texture);this.bind(m,'WGPU.Texture','Dispose',()=>texture.dispose());return m;});this.bind(tm,'WGPU.Target','Clear',c=>{if(!Array.isArray(c)||c.length!==4||c.some(v=>!Number.isFinite(v)||v<0||v>1))throw new Error('Color needs four RGBA values in 0..1');target.clear(c);});this.bind(tm,'WGPU.Target','Draw',(shader,vertices)=>{if(!Array.isArray(vertices)||vertices.length===0||vertices.length%36||vertices.some(v=>!Number.isFinite(v)))throw new Error('Triangle vertices need a multiple of 36 finite floats');const obj=this.objects.get(shader);if(!obj)throw new Error('Expected a live shader');if(typeof target.drawModule==='function')target.drawModule(obj,vertices);else target.draw(obj,vertices);});
    const DEPTH=['none','less','read'],BLEND=['alpha','add','multiply','screen','replace','premultiplied'],CULL=['none','back','front'];
    this.bind(tm,'WGPU.Target','SetState',state=>{if(!(state instanceof Map))throw new Error('SetState expects a hash such as {"depth":"less","blend":"replace","cull":"back"}');const out={depth:0,blend:0,cull:0};
      for(const [key,value] of state.values()){const list=key==='depth'?DEPTH:key==='blend'?BLEND:key==='cull'?CULL:null;if(!list)throw new Error(`Unknown draw state key '${key}'; expected depth, blend or cull`);const i=list.indexOf(value);if(i<0)throw new Error(`Unknown ${key} mode '${value}'; expected ${list.join(', ')}`);out[key]=i;}
      if(typeof target.setState!=='function')throw new Error('Draw state is unavailable on this platform');target.setState(out);});
    this.bind(tm,'WGPU.Target','DrawBuffer',(shader,buffer,first=0,count)=>{const s=this.objects.get(shader),vb=this.objects.get(buffer);if(!s)throw new Error('Expected a live shader');if(!vb||vb.disposed)throw new Error('Expected a live vertex buffer from this wgpu device');
      const vertices=vb.length/12;count??=vertices-first;if(!Number.isInteger(first)||!Number.isInteger(count)||first<0||count<0||first>vertices-count)throw new Error('Vertex range is outside the buffer / 頂點範圍超出緩衝區');if(count>0)target.drawRetained(s,vb,first,count);});
    this.bind(tm,'WGPU.Target','DrawProcedural',(shader,count,first=0)=>{const s=this.objects.get(shader);if(!s)throw new Error('Expected a live shader');if(!Number.isInteger(first)||!Number.isInteger(count)||first<0||count<0||count>48*1024*1024)throw new Error('Procedural vertex range must be nonnegative and at most 50331648 vertices');if(count>0)target.drawRetained(s,null,first,count);});this.bind(tm,'WGPU.Target','ReadPixels',async()=>{const bytes=await target.readPixels();return Array.from({length:bytes.length/4},(_,i)=>pack(Array.from(bytes.subarray(i*4,i*4+4),v=>v/255)));});this.bind(tm,'WGPU.Target','Save',async path=>this.saveImage(path,w,h,await target.readPixels()));this.bind(tm,'WGPU.Target','Dispose',()=>target.dispose());return tm;});return dm;
  });}
  registerMath(){const m=this.library('GLM'),b=(n,f)=>this.bind(m,'GLM',n,f);this.set(m,'PI',Math.PI);b('Radians',n=>n*Math.PI/180);b('Sin',Math.sin);b('Cos',Math.cos);b('Sqrt',Math.sqrt);b('Dot',vec3.dot);b('Cross',(a,b)=>Array.from(vec3.cross(vec3.create(),a,b)));b('Normalize',a=>{vector(a);if(!Math.hypot(...a))throw new Error('Cannot normalize zero vector');return Array.from(vec3.normalize(vec3.create(),a));});b('Identity',()=>Array.from(mat4.create()));b('Translate',v=>Array.from(mat4.fromTranslation(mat4.create(),v)));b('Scale',v=>Array.from(mat4.fromScaling(mat4.create(),v)));b('Rotate',(angle,axis)=>Array.from(mat4.fromRotation(mat4.create(),angle,axis)));b('Multiply',(a,b)=>Array.from(mat4.multiply(mat4.create(),a,b)));b('LookAt',(e,t,u)=>Array.from(mat4.lookAt(mat4.create(),e,t,u)));b('Perspective',(f,a,n,z)=>Array.from(mat4.perspectiveNO(mat4.create(),f,a,n,z)));b('Transpose',a=>Array.from(mat4.transpose(mat4.create(),a)));b('Inverse',a=>{const out=mat4.invert(mat4.create(),a);if(!out)throw new Error('Matrix is singular');return Array.from(out);});b('TransformPoint',(m,v)=>Array.from(vec3.transformMat4(vec3.create(),v,m)));}
  registerText(){const m=this.library('TextInput');this.bind(m,'TextInput','Backspace',text=>{const segments=[...new Intl.Segmenter(undefined,{granularity:'grapheme'}).segment(text)];return segments.slice(0,-1).map(s=>s.segment).join('');});this.bind(m,'TextInput','Length',text=>num([...new Intl.Segmenter(undefined,{granularity:'grapheme'}).segment(text)].length,'int'));}
  asset(path){const name=lookupPath(this.bundle.assets,this.resolve(path));const value=this.bundle.assets[name];return value instanceof Uint8Array?value.slice():Uint8Array.from(atob(value),c=>c.charCodeAt(0));}
  async loadTexture(device,descriptor,path){return device.createTexture(descriptor,await this.textureData(descriptor,path));}
  async textureData(descriptor,path){return decodeTextureDataAsync(descriptor,this.asset(path),this.signal);}
  async dispose(){
    await this.runtime.dispose?.();
    this.network?.dispose();
    this.runtime.cancelled=true;
    try{await this.dynamicLibraries?.dispose();}
    finally{
      try{this.webcam?.dispose();this.camera?.dispose();await this.pose?.dispose();await this.onnx?.dispose();await this.audio?.dispose();await this.application?.dispose();}
      finally{this.processing.dispose();this.graphics.dispose();this.legacyDispose?.();}
    }
  }
}
