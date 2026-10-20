import {mat4} from './vendor-gl-matrix.mjs';
export {uniformLayout} from './shader-layout.mjs';
import {uniformLayout,safeWGSL} from './shader-layout.mjs';
import {textureDescriptor,textureLayout,storageLayout,validateTextureBytes} from './wgpu-textures.mjs';
import {BrowserWgpuWindow} from './wgpu-window.mjs';
export class BrowserWgpuBackend {
  constructor(abi) { this.abi=abi;this.name='webgpu';this.devices=new Set(); }
  async createDevice() {
    if(!navigator.gpu)throw new Error('WebGPU is unavailable. Use a WebGPU browser on HTTPS or localhost.');
    const adapter=await navigator.gpu.requestAdapter();if(!adapter)throw new Error('WebGPU adapter unavailable');
    const gpu=await adapter.requestDevice(),device=new BrowserWgpuDevice(gpu,this.abi);this.devices.add(device);return device;
  }
  createWindow(width,height,title){return new BrowserWgpuWindow(document.getElementById('surfaces'),width,height,title);}
  async createDeviceForWindow(window){
    if(!(window instanceof BrowserWgpuWindow)||window.disposed)throw new Error('Expected a live WGPU window');
    if(window.device&&!window.device.disposed)throw new Error('This window already has a wgpu device; dispose it first');
    const device=await this.createDevice();device.window=window;window.device=device;device.windowSurface=new BrowserWindowSurface(device,window.canvas);return device;
  }
  dispose(){for(const device of this.devices)device.dispose();this.devices.clear();}
}
export class BrowserWgpuDevice {
  constructor(gpu,abi) {
    this.gpu=gpu;this.abi=abi;this.backend='webgpu';this.resources=new Set();this.disposed=false;this.failure=null;
    gpu.addEventListener('uncapturederror',e=>{this.failure=e.error.message;});gpu.lost.then(info=>{if(!this.disposed)this.failure=`WebGPU device lost: ${info.message}`;});
    this.layout=gpu.createBindGroupLayout({entries:[
      {binding:0,visibility:3,buffer:{type:'uniform',minBindingSize:abi.uniformBytes}},
      {binding:1,visibility:2,texture:{sampleType:'float'}},{binding:2,visibility:2,sampler:{type:'filtering'}}]});
    this.userLayout=gpu.createBindGroupLayout({entries:[{binding:0,visibility:3,buffer:{type:'uniform',minBindingSize:1024}}]});
    this.pipelineLayout=gpu.createPipelineLayout({bindGroupLayouts:[this.layout,this.userLayout]});
    this.sampler=gpu.createSampler({magFilter:'linear',minFilter:'linear'});
    this.white=this.texture(1,1,new Uint8Array([255,255,255,255]));
  }
  live(){if(this.disposed)throw new Error('wgpu device has been disposed');if(this.failure)throw new Error(this.failure);}
  texture(w,h,data,format="rgba8unorm"){this.live();const t=this.gpu.createTexture({size:[w,h],format,usage:GPUTextureUsage.TEXTURE_BINDING|GPUTextureUsage.COPY_DST|GPUTextureUsage.COPY_SRC|GPUTextureUsage.RENDER_ATTACHMENT});this.resources.add(t);if(data)this.gpu.queue.writeTexture({texture:t},data,{bytesPerRow:w*4},[w,h]);return t;}
  createTexture(descriptor,bytes){this.live();validateTextureBytes(descriptor,bytes);const gpu=this.gpu,t=gpu.createTexture({size:[descriptor.width,descriptor.height,descriptor.layers],format:descriptor.format,mipLevelCount:descriptor.mipLevels,usage:GPUTextureUsage.TEXTURE_BINDING|GPUTextureUsage.COPY_DST});
    let offset=0;for(let mip=0;mip<descriptor.mipLevels;mip++){const w=Math.max(1,descriptor.width>>mip),h=Math.max(1,descriptor.height>>mip),size=w*h*descriptor.layers*descriptor.bytesPerPixel;gpu.queue.writeTexture({texture:t,mipLevel:mip},bytes.subarray(offset,offset+size),{bytesPerRow:w*descriptor.bytesPerPixel,rowsPerImage:h},[w,h,descriptor.layers]);offset+=size;}
    const texture={owner:this,descriptor,handle:t,view:t.createView({dimension:descriptor.dimension}),disposed:false,dispose:()=>{if(texture.disposed)return;this.flush();texture.disposed=true;t.destroy();this.resources.delete(texture);}};this.resources.add(texture);return texture;
  }
  buffer(data,usage){this.live();const bytes=ArrayBuffer.isView(data)?data:new Float32Array(data);if(bytes.byteLength<1||bytes.byteLength>64*1024*1024)throw new Error('wgpu buffer size must be 1..64 MiB');const b=this.gpu.createBuffer({size:Math.ceil(bytes.byteLength/4)*4,usage:usage|GPUBufferUsage.COPY_DST});this.gpu.queue.writeBuffer(b,0,bytes);return b;}
  createBuffer(values){const b=new BrowserBuffer(this,values);this.resources.add(b);return b;}
  // Replaced cached uniform buffers may still be referenced by open passes; destroy them after the next submission completes.
  retire(buffer){(this.retired??=[]).push(buffer);}
  releaseRetired(){const r=this.retired;if(!r?.length)return;this.retired=[];this.flush();this.gpu.queue.onSubmittedWorkDone().then(()=>r.forEach(b=>b.destroy()));}
  createVertexBuffer(values){const b=new BrowserVertexBuffer(this,values);this.resources.add(b);return b;}
  async present(target){if(!this.windowSurface)throw new Error('This wgpu device has no window surface; create it with WGPU.CreateDevice(window)');if(target.owner!==this)throw new Error('Expected a render target from this wgpu device');this.live();await this.windowSurface.present(target);}
  // Group 0 for WGPU-module draws: identity Processing matrices, white texture and the shared sampler.
  identityGroup(){if(!this.identity){this.identity=this.gpu.createBindGroup({layout:this.layout,entries:[{binding:0,resource:{buffer:this.buffer(defaultUniforms(this.abi),GPUBufferUsage.UNIFORM)}},{binding:1,resource:this.white.createView()},{binding:2,resource:this.sampler}]});}return this.identity;}
  async compile(source){this.live();const shader=this.gpu.createShaderModule({code:safeWGSL(source)}),info=await shader.getCompilationInfo();const errors=info.messages.filter(m=>m.type==='error');if(errors.length)throw new Error(`Shader compile failed: ${errors.map(e=>e.message).join('\n')}`);return shader;}
  async createShader(vertex,fragment){const s=new BrowserShader(this,vertex,fragment);s.vertex=await this.compile(this.abi.header+vertex);s.fragment=await this.compile(this.abi.header+fragment);this.resources.add(s);return s;}
  createTarget(w,h,format="rgba8unorm"){const t=new BrowserTarget(this,w,h,format);this.resources.add(t);return t;}
  createSurface(canvas){return new BrowserWgpuSurface(this,canvas);}
  flush(){this.live();for(const r of this.resources)if(r instanceof BrowserTarget)r.flush();}
  async poll(){this.flush();this.releaseRetired();await this.gpu.queue.onSubmittedWorkDone();this.live();}
  async readBuffer(source,size,offset=0){this.flush();const b=this.gpu.createBuffer({size,usage:GPUBufferUsage.COPY_DST|GPUBufferUsage.MAP_READ});try{const encoder=this.gpu.createCommandEncoder();encoder.copyBufferToBuffer(source,offset,b,0,size);this.gpu.queue.submit([encoder.finish()]);await b.mapAsync(GPUMapMode.READ);return new Uint8Array(b.getMappedRange()).slice();}finally{b.destroy();}}
  async dispatch(source,buffer,groups){this.live();buffer.live();if(buffer.owner!==this||!Number.isInteger(groups)||groups<1||groups>65535)throw new Error('Invalid compute dispatch');this.flush();this.computePipelines??=new Map();let p=this.computePipelines.get(source);if(!p){const shader=await this.compile(source);p=await this.gpu.createComputePipelineAsync({layout:'auto',compute:{module:shader,entryPoint:'cs_main'}});if(this.computePipelines.size>=32)this.computePipelines.delete(this.computePipelines.keys().next().value);this.computePipelines.set(source,p);}const group=this.gpu.createBindGroup({layout:p.getBindGroupLayout(0),entries:[{binding:0,resource:{buffer:buffer.handle}}]});const e=this.gpu.createCommandEncoder(),pass=e.beginComputePass();pass.setPipeline(p);pass.setBindGroup(0,group);pass.dispatchWorkgroups(groups);pass.end();this.gpu.queue.submit([e.finish()]);}
  dispose(){if(this.disposed)return;this.failure=null;this.flush();for(const r of [...this.resources])r.dispose?r.dispose():r.destroy();this.resources.clear();this.computePipelines?.clear();this.windowSurface?.dispose();this.windowSurface=null;if(this.window){this.window.device=null;this.window=null;}this.disposed=true;this.gpu.destroy();}
}
// Scales any render target onto a window canvas with a linear blit (render scale independent of the window).
class BrowserWindowSurface {
  constructor(owner,canvas){
    this.owner=owner;this.canvas=canvas;this.context=canvas.getContext('webgpu');if(!this.context)throw new Error('WebGPU canvas unavailable');
    this.format=navigator.gpu.getPreferredCanvasFormat?.()??'bgra8unorm';this.configured=false;
    const g=owner.gpu,code=`@group(0) @binding(0) var image: texture_2d<f32>;@group(0) @binding(1) var imageSampler: sampler;
struct Output { @builtin(position) position: vec4<f32>, @location(0) uv: vec2<f32> };
@vertex fn vs_main(@builtin(vertex_index) index: u32) -> Output { let x=f32((index<<1u)&2u); let y=f32(index&2u); var o: Output; o.position=vec4<f32>(x*2.0-1.0,1.0-y*2.0,0.0,1.0); o.uv=vec2<f32>(x,y); return o; }
@fragment fn fs_main(i: Output) -> @location(0) vec4<f32> { return vec4<f32>(textureSample(image,imageSampler,i.uv).rgb,1.0); }`;
    const module=g.createShaderModule({code});this.pipeline=g.createRenderPipeline({layout:'auto',vertex:{module,entryPoint:'vs_main'},fragment:{module,entryPoint:'fs_main',targets:[{format:this.format}]},primitive:{topology:'triangle-list'}});
    this.sampler=g.createSampler({magFilter:'linear',minFilter:'linear'});
  }
  async present(target){
    if(this.canvas.width===0||this.canvas.height===0)return;
    if(!this.configured){this.context.configure({device:this.owner.gpu,format:this.format,alphaMode:'opaque'});this.configured=true;}
    this.owner.flush();const g=this.owner.gpu,encoder=g.createCommandEncoder();
    const pass=encoder.beginRenderPass({colorAttachments:[{view:this.context.getCurrentTexture().createView(),loadOp:'clear',storeOp:'store',clearValue:[0,0,0,1]}]});
    pass.setPipeline(this.pipeline);pass.setBindGroup(0,g.createBindGroup({layout:this.pipeline.getBindGroupLayout(0),entries:[{binding:0,resource:target.color.createView()},{binding:1,resource:this.sampler}]}));pass.draw(3);pass.end();
    g.queue.submit([encoder.finish()]);this.owner.releaseRetired();
    // Keep at most two frames in flight so input latency stays bounded on slow GPUs.
    this.pending??=[];const done=g.queue.onSubmittedWorkDone();this.pending.push(done);if(this.pending.length>2)await this.pending.shift();
  }
  dispose(){try{this.context.unconfigure();}catch{}}
}
// Presentation belongs to the GPU backend. Processing passes a render target;
// it does not configure GPUCanvasContext or expose it to the language runtime.
class BrowserWgpuSurface {
  constructor(owner,canvas){
    this.owner=owner;this.canvas=canvas;this.context=canvas.getContext('webgpu');
    if(!this.context)throw new Error('WebGPU canvas unavailable');
    this.context.configure({device:owner.gpu,format:'rgba8unorm',alphaMode:'premultiplied',usage:GPUTextureUsage.RENDER_ATTACHMENT|GPUTextureUsage.COPY_DST});
  }
  async present(target){
    if(this.canvas.width===0||this.canvas.height===0)return;
    if(target.owner!==this.owner||target.width!==this.canvas.width||target.height!==this.canvas.height)throw new Error('Presentation requires a target matching the surface pixel size');
    this.owner.flush();const encoder=this.owner.gpu.createCommandEncoder();
    encoder.copyTextureToTexture({texture:target.color},{texture:this.context.getCurrentTexture()},[target.width,target.height]);
    this.owner.gpu.queue.submit([encoder.finish()]);
    this.presentations??=new Set();const done=this.owner.gpu.queue.onSubmittedWorkDone();this.presentations.add(done);
    done.then(()=>this.presentations.delete(done),()=>this.presentations.delete(done));
    if(this.presentations.size>=2)await this.presentations.values().next().value;
  }
  dispose(){this.context.unconfigure();}
}
class BrowserBuffer {
  constructor(owner,values){this.owner=owner;this.length=typeof values==='number'?values:values.length;this.handle=owner.buffer(typeof values==='number'?new Float32Array(values):new Float32Array(values),GPUBufferUsage.STORAGE|GPUBufferUsage.COPY_SRC);}
  live(){this.owner.live();if(!this.handle)throw new Error('wgpu buffer has been disposed');}
  write(v){this.live();if(v.length!==this.length)throw new Error(`Buffer Write needs ${this.length} floats`);this.owner.flush();this.owner.gpu.queue.writeBuffer(this.handle,0,new Float32Array(v));}
  async read(){this.live();const b=await this.owner.readBuffer(this.handle,this.length*4);return Array.from(new Float32Array(b.buffer));}
  range(offset,count){this.live();if(!Number.isInteger(offset)||!Number.isInteger(count)||offset<0||count<1||offset>this.length-count)throw new Error('Invalid buffer range / 緩衝區範圍無效');}
  writeRange(offset,values){this.range(offset,values.length);if(values.some(v=>typeof v!=='number'||!Number.isFinite(v)||!Number.isFinite(Math.fround(v))))throw new Error('Expected finite floats');this.owner.flush();this.owner.gpu.queue.writeBuffer(this.handle,offset*4,new Float32Array(values));}
  async readRange(offset,count){this.range(offset,count);const b=await this.owner.readBuffer(this.handle,count*4,offset*4);return Array.from(new Float32Array(b.buffer));}
  dispose(){if(!this.handle)return;this.owner.flush();this.handle.destroy();this.handle=null;this.owner.resources.delete(this);}
}
class BrowserVertexBuffer {
  constructor(owner,values){if(!values.length||values.length%12)throw new Error('Vertex buffers need a multiple of 12 floats');this.owner=owner;this.length=values.length;this.handle=owner.buffer(new Float32Array(values),GPUBufferUsage.VERTEX);}
  get disposed(){return !this.handle;}
  live(){this.owner.live();if(!this.handle)throw new Error('wgpu vertex buffer has been disposed');}
  write(v){this.live();if(v.length!==this.length)throw new Error(`Vertex buffer Write needs ${this.length} floats`);this.owner.flush();this.owner.gpu.queue.writeBuffer(this.handle,0,new Float32Array(v));}
  writeRange(offset,values){this.live();if(!Number.isInteger(offset)||offset<0||!values.length||offset>this.length-values.length)throw new Error('Invalid buffer range / 緩衝區範圍無效');this.owner.flush();this.owner.gpu.queue.writeBuffer(this.handle,offset*4,new Float32Array(values));}
  dispose(){if(!this.handle)return;this.owner.flush();this.handle.destroy();this.handle=null;this.owner.resources.delete(this);}
}
class BrowserShader {
  constructor(owner,vertex,fragment){this.owner=owner;this.preserveDrawBoundaries=/vertex_index|primitive_index/.test(vertex+'\n'+fragment);this.disposed=false;this.fields=uniformLayout(vertex+'\n'+fragment);this.bytes=new Uint8Array(1024);this.pipelines=new Map();this.textureFields=textureLayout(vertex+'\n'+fragment);this.textures=new Map();this.bufferFields=storageLayout(vertex+'\n'+fragment);this.buffers=new Map();this.version=0;
    const entries=[...this.textureFields.values()].map(f=>({binding:f.binding,visibility:3,texture:{sampleType:'float',viewDimension:f.dimension}}));for(const binding of this.bufferFields.values())entries.push({binding,visibility:3,buffer:{type:'read-only-storage'}});entries.push({binding:8,visibility:3,sampler:{type:'filtering'}});
    this.textureLayout=owner.gpu.createBindGroupLayout({entries});this.pipelineLayout=owner.gpu.createPipelineLayout({bindGroupLayouts:[owner.layout,owner.userLayout,this.textureLayout]});this.textureSampler=owner.gpu.createSampler({magFilter:'linear',minFilter:'linear',mipmapFilter:'linear'});
  }
  setTexture(name,texture){this.owner.live();const field=this.textureFields.get(name);if(this.disposed||!field||!texture||texture.disposed||texture.owner!==this.owner||texture.descriptor.dimension!==field.dimension)throw new Error('Unknown texture name, foreign device, disposed or incompatible texture');this.textures.set(name,texture);}
  prepareTextures(destination){for(const t of this.textures.values())if(t.sourceTarget){if(t.sourceTarget===destination)throw new Error("A render target cannot sample its own color attachment");t.sourceTarget.flush();}}
  setBuffer(name,buffer){this.owner.live();if(this.disposed||!this.bufferFields.has(name)||!buffer||!buffer.handle||buffer.owner!==this.owner)throw new Error('Unknown storage buffer name, foreign device or disposed buffer');this.buffers.set(name,buffer);}
  // User uniforms are uploaded once per set() version and shared by later retained draws.
  userGroup(){if(!this.cachedUser||this.cachedVersion!==this.version){const g=this.owner.gpu;if(this.cachedBuffer)this.owner.retire(this.cachedBuffer);this.cachedBuffer=this.owner.buffer(this.bytes,GPUBufferUsage.UNIFORM);this.cachedUser=g.createBindGroup({layout:this.owner.userLayout,entries:[{binding:0,resource:{buffer:this.cachedBuffer}}]});this.cachedVersion=this.version;}return this.cachedUser;}
  textureGroup(){const entries=[];for(const [name,f] of this.textureFields){const t=this.textures.get(name);if(!t||t.disposed)throw new Error('Missing or disposed shader texture: '+name);entries.push({binding:f.binding,resource:t.view});}for(const [name,binding] of this.bufferFields){const b=this.buffers.get(name);if(!b||!b.handle)throw new Error('Missing or disposed shader storage buffer: '+name);entries.push({binding,resource:{buffer:b.handle}});}entries.push({binding:8,resource:this.textureSampler});return this.owner.gpu.createBindGroup({layout:this.textureLayout,entries});}
  set(name,values,integer=false){this.owner.live();if(this.disposed)throw new Error('wgpu shader has been disposed');const f=this.fields.get(name);if(!f)throw new Error(`Unknown UserUniforms field: ${name}`);values=Array.isArray(values)?values:[values];if(values.length!==f.count)throw new Error(`Uniform ${name} needs ${f.count} values`);if(integer!==['i32','u32'].includes(f.type))throw new Error(`Uniform ${name} requires ${integer?'set':'setInt'}()`);if(f.type==='u32'&&values[0]<0)throw new Error('u32 uniform must be nonnegative');const view=new DataView(this.bytes.buffer);values.forEach((v,i)=>integer?view.setInt32(f.offset+i*4,v,true):view.setFloat32(f.offset+i*4,v,true));this.version++;}
  // depth: false/0 none, true/1 test+write, 2 test only. cull: 0 none, 1 back, 2 front. blend 5 is premultiplied alpha.
  pipeline(depth,blend=0,mode=4,format="rgba8unorm",cull=0,vertexBuffer=true){depth=depth===true?1:depth===false?0:depth;const key=`${depth}:${blend}:${mode}:${format}:${cull}:${vertexBuffer}`;if(this.pipelines.has(key))return this.pipelines.get(key);const source=blend===2?'dst':blend===3||blend===4||blend===5?'one':'src-alpha',dest=blend===1?'one':blend===2||blend===4?'zero':blend===3?'one-minus-src':'one-minus-src-alpha';
    const buffers=vertexBuffer?[{arrayStride:48,attributes:[{shaderLocation:0,offset:0,format:'float32x3'},{shaderLocation:1,offset:12,format:'float32x3'},{shaderLocation:2,offset:24,format:'float32x2'},{shaderLocation:3,offset:32,format:'float32x4'}]}]:[];
    const p=this.owner.gpu.createRenderPipeline({layout:this.pipelineLayout,vertex:{module:this.vertex,entryPoint:'vs_main',buffers},fragment:{module:this.fragment,entryPoint:'fs_main',targets:[{format,blend:{color:{srcFactor:source,dstFactor:dest,operation:'add'},alpha:{srcFactor:'one',dstFactor:blend===1?'one':blend===4?'zero':'one-minus-src-alpha',operation:'add'}}}]},primitive:{topology:mode===1?'line-list':mode===0?'point-list':'triangle-list',frontFace:'ccw',cullMode:cull===1?'back':cull===2?'front':'none'},depthStencil:{format:'depth24plus',depthWriteEnabled:depth===1,depthCompare:depth?'less-equal':'always'}});this.pipelines.set(key,p);return p;
  }
  dispose(){this.disposed=true;this.pipelines.clear();this.owner.resources.delete(this);}
}
export class BrowserTarget {
  constructor(owner,w,h,format="rgba8unorm"){if(!["rgba8unorm","rgba16float"].includes(format))throw new Error("Invalid render target format");this.format=format;if(!Number.isInteger(w)||!Number.isInteger(h)||w<1||h<1||w>8192||h>8192||w*h>16*1024*1024)throw new Error('Invalid render target dimensions');this.owner=owner;this.width=w;this.height=h;this.color=owner.texture(w,h,null,format);this.depth=owner.gpu.createTexture({size:[w,h],format:'depth24plus',usage:GPUTextureUsage.RENDER_ATTACHMENT});this.pending=[];this.disposed=false;this.first=true;}
  live(){this.owner.live();if(this.disposed)throw new Error('wgpu render target has been disposed');}
  getTexture(){this.live();const target=this;return {owner:this.owner,descriptor:textureDescriptor(this.width,this.height,this.format),sourceTarget:this,get disposed(){return target.disposed;},get view(){return target.color.createView();},dispose:()=>this.dispose()};}
  begin(clear){this.live();this.encoder=this.owner.gpu.createCommandEncoder();this.pass=this.encoder.beginRenderPass({colorAttachments:[{view:this.color.createView(),loadOp:clear||this.first?'clear':'load',storeOp:'store',clearValue:clear??[0,0,0,0]}],depthStencilAttachment:{view:this.depth.createView(),depthLoadOp:clear||this.first?'clear':'load',depthStoreOp:'store',depthClearValue:1}});this.first=false;}
  clear(c){this.flush();this.begin(c);}
  setState(state){this.live();this.state=state;}
  drawModule(shader,vertices){const s=this.state??{depth:0,blend:0,cull:0};this.draw(shader,vertices,null,s.depth,null,s.blend,4,null,s.cull);}
  drawRetained(shader,buffer,first,count){this.live();if(shader.owner!==this.owner||shader.disposed)throw new Error('Expected a live shader from this wgpu device');if(buffer){buffer.live();if(buffer.owner!==this.owner)throw new Error('Expected a vertex buffer from this wgpu device');}
    shader.prepareTextures(this);if(!this.pass)this.begin();const s=this.state??{depth:0,blend:0,cull:0};
    this.pass.setPipeline(shader.pipeline(s.depth,s.blend,4,this.format,s.cull,!!buffer));this.pass.setBindGroup(0,this.owner.identityGroup());this.pass.setBindGroup(1,shader.userGroup());this.pass.setBindGroup(2,shader.textureGroup());
    if(buffer)this.pass.setVertexBuffer(0,buffer.handle);this.pass.setScissorRect(0,0,this.width,this.height);this.pass.draw(count,1,first);}
  draw(shader,vertices,uniforms,depth=false,texture=null,blend=0,mode=4,clip=null,cull=0){this.live();if(shader.owner!==this.owner||shader.disposed)throw new Error('Expected a live shader from this wgpu device');shader.prepareTextures(this);if(!this.pass)this.begin();const d=this.owner,g=d.gpu;uniforms??=defaultUniforms(d.abi);const vb=d.buffer(new Float32Array(vertices),GPUBufferUsage.VERTEX),ub=d.buffer(uniforms,GPUBufferUsage.UNIFORM),user=d.buffer(shader.bytes,GPUBufferUsage.UNIFORM);this.pending.push(vb,ub,user);const group=g.createBindGroup({layout:d.layout,entries:[{binding:0,resource:{buffer:ub}},{binding:1,resource:(texture??d.white).createView()},{binding:2,resource:d.sampler}]});const ug=g.createBindGroup({layout:d.userLayout,entries:[{binding:0,resource:{buffer:user}}]});this.pass.setPipeline(shader.pipeline(depth,blend,mode,this.format,cull));this.pass.setBindGroup(0,group);this.pass.setBindGroup(1,ug);this.pass.setBindGroup(2,shader.textureGroup());this.pass.setVertexBuffer(0,vb);
    // Match the desktop backend's physical scissor bounds, including fractional
    // density, negative origins and clips beyond the resized surface.
    const bound=(n,max)=>Math.trunc(Math.max(0,Math.min(max,n)));
    const x=clip?bound(clip[0],this.width):0,y=clip?bound(clip[1],this.height):0;
    const right=clip?bound(clip[0]+clip[2],this.width):this.width,bottom=clip?bound(clip[1]+clip[3],this.height):this.height;
    const width=Math.max(0,right-x),height=Math.max(0,bottom-y);
    this.pass.setScissorRect(x,y,width,height);if(width>0&&height>0)this.pass.draw(vertices.length/12);
  }
  flush(){if(!this.pass)return;this.pass.end();this.owner.gpu.queue.submit([this.encoder.finish()]);const pending=this.pending;this.pending=[];this.owner.gpu.queue.onSubmittedWorkDone().then(()=>pending.forEach(b=>b.destroy()));this.pass=null;this.encoder=null;}
  async readPixels(){this.live();if(this.format!=="rgba8unorm")throw new Error("Tone-map HDR targets into rgba8unorm before reading pixels");this.owner.flush();const pitch=Math.ceil(this.width*4/256)*256,size=pitch*this.height,g=this.owner.gpu,b=g.createBuffer({size,usage:GPUBufferUsage.COPY_DST|GPUBufferUsage.MAP_READ});try{const e=g.createCommandEncoder();e.copyTextureToBuffer({texture:this.color},{buffer:b,bytesPerRow:pitch},[this.width,this.height]);g.queue.submit([e.finish()]);await b.mapAsync(GPUMapMode.READ);const raw=new Uint8Array(b.getMappedRange()),out=new Uint8Array(this.width*this.height*4);for(let y=0;y<this.height;y++)out.set(raw.subarray(y*pitch,y*pitch+this.width*4),y*this.width*4);return out;}finally{b.destroy();}}
  dispose(){if(this.disposed)return;this.flush();this.color.destroy();this.depth.destroy();this.owner.resources.delete(this.color);this.owner.resources.delete(this);this.disposed=true;}
}
export {defaultUniforms} from './wgpu-uniforms.mjs';


import {defaultUniforms} from './wgpu-uniforms.mjs';
