import {mat4} from './vendor-gl-matrix.mjs';
export function uniformLayout(source) {
  source=source.replace(/\/\*[\s\S]*?\*\/|\/\/[^\r\n]*/g,'');
  const body=source.match(/struct\s+UserUniforms\s*\{([^}]*)\}/)?.[1],fields=new Map();let offset=0;
  for(const text of body?.split(',').filter(x=>x.trim())??[]) {
    const m=text.match(/^\s*([\p{L}\p{N}_]+)\s*:\s*(f32|i32|u32|vec[234]<f32>|mat4x4<f32>)\s*$/u);
    if(!m)throw new Error('Unsupported UserUniforms field');
    const count=m[2].startsWith('mat')?16:m[2].startsWith('vec')?Number(m[2][3]):1,align=count===1?4:count===2?8:16;
    offset=Math.ceil(offset/align)*align;if(offset+count*4>1024||fields.has(m[1]))throw new Error('Duplicate or oversized UserUniforms field');
    fields.set(m[1],{offset,count,type:m[2]});offset+=count*4;
  }
  return fields;
}
const safeWGSL = source => { const ids=new Map();return source.replace(/[\p{L}_][\p{L}\p{N}_]*/gu,s=>/[^\x00-\x7f]/.test(s)?(ids.has(s)?ids.get(s):(ids.set(s,`aqua_${ids.size}`),ids.get(s))):s); };
export class BrowserWgpuBackend {
  constructor(abi) { this.abi=abi;this.name='webgpu';this.devices=new Set(); }
  async createDevice() {
    if(!navigator.gpu)throw new Error('WebGPU is unavailable. Use a WebGPU browser on HTTPS or localhost.');
    const adapter=await navigator.gpu.requestAdapter();if(!adapter)throw new Error('WebGPU adapter unavailable');
    const gpu=await adapter.requestDevice(),device=new BrowserWgpuDevice(gpu,this.abi);this.devices.add(device);return device;
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
  texture(w,h,data){this.live();const t=this.gpu.createTexture({size:[w,h],format:'rgba8unorm',usage:GPUTextureUsage.TEXTURE_BINDING|GPUTextureUsage.COPY_DST|GPUTextureUsage.COPY_SRC|GPUTextureUsage.RENDER_ATTACHMENT});this.resources.add(t);if(data)this.gpu.queue.writeTexture({texture:t},data,{bytesPerRow:w*4},[w,h]);return t;}
  buffer(data,usage){this.live();const bytes=ArrayBuffer.isView(data)?data:new Float32Array(data);if(bytes.byteLength<1||bytes.byteLength>64*1024*1024)throw new Error('wgpu buffer size must be 1..64 MiB');const b=this.gpu.createBuffer({size:Math.ceil(bytes.byteLength/4)*4,usage:usage|GPUBufferUsage.COPY_DST});this.gpu.queue.writeBuffer(b,0,bytes);return b;}
  createBuffer(values){const b=new BrowserBuffer(this,values);this.resources.add(b);return b;}
  async compile(source){this.live();const shader=this.gpu.createShaderModule({code:safeWGSL(source)}),info=await shader.getCompilationInfo();const errors=info.messages.filter(m=>m.type==='error');if(errors.length)throw new Error(`Shader compile failed: ${errors.map(e=>e.message).join('\n')}`);return shader;}
  async createShader(vertex,fragment){const s=new BrowserShader(this,vertex,fragment);s.vertex=await this.compile(this.abi.header+vertex);s.fragment=await this.compile(this.abi.header+fragment);this.resources.add(s);return s;}
  createTarget(w,h){const t=new BrowserTarget(this,w,h);this.resources.add(t);return t;}
  createSurface(canvas){return new BrowserWgpuSurface(this,canvas);}
  flush(){this.live();for(const r of this.resources)if(r instanceof BrowserTarget)r.flush();}
  async poll(){this.flush();await this.gpu.queue.onSubmittedWorkDone();this.live();}
  async readBuffer(source,size){this.flush();const b=this.gpu.createBuffer({size,usage:GPUBufferUsage.COPY_DST|GPUBufferUsage.MAP_READ});try{const encoder=this.gpu.createCommandEncoder();encoder.copyBufferToBuffer(source,0,b,0,size);this.gpu.queue.submit([encoder.finish()]);await b.mapAsync(GPUMapMode.READ);return new Uint8Array(b.getMappedRange()).slice();}finally{b.destroy();}}
  async dispatch(source,buffer,groups){this.live();buffer.live();if(buffer.owner!==this)throw new Error('Buffer belongs to another wgpu device');this.flush();const shader=await this.compile(source);const p=this.gpu.createComputePipeline({layout:'auto',compute:{module:shader,entryPoint:'cs_main'}});const group=this.gpu.createBindGroup({layout:p.getBindGroupLayout(0),entries:[{binding:0,resource:{buffer:buffer.handle}}]});const e=this.gpu.createCommandEncoder(),pass=e.beginComputePass();pass.setPipeline(p);pass.setBindGroup(0,group);pass.dispatchWorkgroups(groups);pass.end();this.gpu.queue.submit([e.finish()]);}
  dispose(){if(this.disposed)return;this.failure=null;this.flush();for(const r of [...this.resources])r.dispose?r.dispose():r.destroy();this.resources.clear();this.disposed=true;this.gpu.destroy();}
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
    this.owner.gpu.queue.submit([encoder.finish()]);await this.owner.poll();
  }
  dispose(){this.context.unconfigure();}
}
class BrowserBuffer {
  constructor(owner,values){this.owner=owner;this.length=values.length;this.handle=owner.buffer(new Float32Array(values),GPUBufferUsage.STORAGE|GPUBufferUsage.COPY_SRC);}
  live(){this.owner.live();if(!this.handle)throw new Error('wgpu buffer has been disposed');}
  write(v){this.live();if(v.length!==this.length)throw new Error(`Buffer Write needs ${this.length} floats`);this.owner.flush();this.owner.gpu.queue.writeBuffer(this.handle,0,new Float32Array(v));}
  async read(){this.live();const b=await this.owner.readBuffer(this.handle,this.length*4);return Array.from(new Float32Array(b.buffer));}
  dispose(){if(!this.handle)return;this.owner.flush();this.handle.destroy();this.handle=null;this.owner.resources.delete(this);}
}
class BrowserShader {
  constructor(owner,vertex,fragment){this.owner=owner;this.disposed=false;this.fields=uniformLayout(vertex+'\n'+fragment);this.bytes=new Uint8Array(1024);this.pipelines=new Map();}
  set(name,values,integer=false){this.owner.live();if(this.disposed)throw new Error('wgpu shader has been disposed');const f=this.fields.get(name);if(!f)throw new Error(`Unknown UserUniforms field: ${name}`);values=Array.isArray(values)?values:[values];if(values.length!==f.count)throw new Error(`Uniform ${name} needs ${f.count} values`);if(integer!==['i32','u32'].includes(f.type))throw new Error(`Uniform ${name} requires ${integer?'set':'setInt'}()`);if(f.type==='u32'&&values[0]<0)throw new Error('u32 uniform must be nonnegative');const view=new DataView(this.bytes.buffer);values.forEach((v,i)=>integer?view.setInt32(f.offset+i*4,v,true):view.setFloat32(f.offset+i*4,v,true));}
  pipeline(depth,blend=0,mode=4){const key=`${depth}:${blend}:${mode}`;if(this.pipelines.has(key))return this.pipelines.get(key);const source=blend===2?'dst':blend===3||blend===4?'one':'src-alpha',dest=blend===1?'one':blend===2||blend===4?'zero':blend===3?'one-minus-src':'one-minus-src-alpha';
    const p=this.owner.gpu.createRenderPipeline({layout:this.owner.pipelineLayout,vertex:{module:this.vertex,entryPoint:'vs_main',buffers:[{arrayStride:48,attributes:[{shaderLocation:0,offset:0,format:'float32x3'},{shaderLocation:1,offset:12,format:'float32x3'},{shaderLocation:2,offset:24,format:'float32x2'},{shaderLocation:3,offset:32,format:'float32x4'}]}]},fragment:{module:this.fragment,entryPoint:'fs_main',targets:[{format:'rgba8unorm',blend:{color:{srcFactor:source,dstFactor:dest,operation:'add'},alpha:{srcFactor:'one',dstFactor:blend===1?'one':blend===4?'zero':'one-minus-src-alpha',operation:'add'}}}]},primitive:{topology:mode===1?'line-list':mode===0?'point-list':'triangle-list'},depthStencil:{format:'depth24plus',depthWriteEnabled:depth,depthCompare:depth?'less-equal':'always'}});this.pipelines.set(key,p);return p;
  }
  dispose(){this.disposed=true;this.pipelines.clear();this.owner.resources.delete(this);}
}
export class BrowserTarget {
  constructor(owner,w,h){if(!Number.isInteger(w)||!Number.isInteger(h)||w<1||h<1||w>8192||h>8192||w*h>16*1024*1024)throw new Error('Invalid render target dimensions');this.owner=owner;this.width=w;this.height=h;this.color=owner.texture(w,h);this.depth=owner.gpu.createTexture({size:[w,h],format:'depth24plus',usage:GPUTextureUsage.RENDER_ATTACHMENT});this.pending=[];this.disposed=false;this.first=true;}
  live(){this.owner.live();if(this.disposed)throw new Error('wgpu render target has been disposed');}
  begin(clear){this.live();this.encoder=this.owner.gpu.createCommandEncoder();this.pass=this.encoder.beginRenderPass({colorAttachments:[{view:this.color.createView(),loadOp:clear||this.first?'clear':'load',storeOp:'store',clearValue:clear??[0,0,0,0]}],depthStencilAttachment:{view:this.depth.createView(),depthLoadOp:clear||this.first?'clear':'load',depthStoreOp:'store',depthClearValue:1}});this.first=false;}
  clear(c){this.flush();this.begin(c);}
  draw(shader,vertices,uniforms,depth=false,texture=null,blend=0,mode=4,clip=null){this.live();if(shader.owner!==this.owner||shader.disposed)throw new Error('Expected a live shader from this wgpu device');if(!this.pass)this.begin();const d=this.owner,g=d.gpu;uniforms??=defaultUniforms(d.abi);const vb=d.buffer(new Float32Array(vertices),GPUBufferUsage.VERTEX),ub=d.buffer(uniforms,GPUBufferUsage.UNIFORM),user=d.buffer(shader.bytes,GPUBufferUsage.UNIFORM);this.pending.push(vb,ub,user);const group=g.createBindGroup({layout:d.layout,entries:[{binding:0,resource:{buffer:ub}},{binding:1,resource:(texture??d.white).createView()},{binding:2,resource:d.sampler}]});const ug=g.createBindGroup({layout:d.userLayout,entries:[{binding:0,resource:{buffer:user}}]});this.pass.setPipeline(shader.pipeline(depth,blend,mode));this.pass.setBindGroup(0,group);this.pass.setBindGroup(1,ug);this.pass.setVertexBuffer(0,vb);
    // Match the desktop backend's physical scissor bounds, including fractional
    // density, negative origins and clips beyond the resized surface.
    const bound=(n,max)=>Math.trunc(Math.max(0,Math.min(max,n)));
    const x=clip?bound(clip[0],this.width):0,y=clip?bound(clip[1],this.height):0;
    const right=clip?bound(clip[0]+clip[2],this.width):this.width,bottom=clip?bound(clip[1]+clip[3],this.height):this.height;
    const width=Math.max(0,right-x),height=Math.max(0,bottom-y);
    this.pass.setScissorRect(x,y,width,height);if(width>0&&height>0)this.pass.draw(vertices.length/12);
  }
  flush(){if(!this.pass)return;this.pass.end();this.owner.gpu.queue.submit([this.encoder.finish()]);const pending=this.pending;this.pending=[];this.owner.gpu.queue.onSubmittedWorkDone().then(()=>pending.forEach(b=>b.destroy()));this.pass=null;this.encoder=null;}
  async readPixels(){this.live();this.owner.flush();const pitch=Math.ceil(this.width*4/256)*256,size=pitch*this.height,g=this.owner.gpu,b=g.createBuffer({size,usage:GPUBufferUsage.COPY_DST|GPUBufferUsage.MAP_READ});try{const e=g.createCommandEncoder();e.copyTextureToBuffer({texture:this.color},{buffer:b,bytesPerRow:pitch},[this.width,this.height]);g.queue.submit([e.finish()]);await b.mapAsync(GPUMapMode.READ);const raw=new Uint8Array(b.getMappedRange()),out=new Uint8Array(this.width*this.height*4);for(let y=0;y<this.height;y++)out.set(raw.subarray(y*pitch,y*pitch+this.width*4),y*this.width*4);return out;}finally{b.destroy();}}
  dispose(){if(this.disposed)return;this.flush();this.color.destroy();this.depth.destroy();this.owner.resources.delete(this.color);this.owner.resources.delete(this);this.disposed=true;}
}
export function defaultUniforms(abi){const u=new Float32Array(abi.uniformBytes/4);for(let i=0;i<4;i++)u.set(mat4.create(),i*16);return u;}

