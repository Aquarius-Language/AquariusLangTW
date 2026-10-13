import {defaultUniforms} from './wgpu-uniforms.mjs';
import {uniformLayout,safeWGSL} from './shader-layout.mjs';
const call=(operation,args={},data=null)=>JSON.parse(native.gpu(operation,JSON.stringify(args),data?encode64(data):''));
const bytes=data=>ArrayBuffer.isView(data)?new Uint8Array(data.buffer,data.byteOffset,data.byteLength):new Uint8Array(data);
export class AndroidWgpuBackend {
  constructor(abi){this.abi=abi;this.name='wgpu-native/Vulkan';this.devices=new Set();}
  async createDevice(){const device=new Device(this.abi);this.devices.add(device);return device;}
  dispose(){for(const d of this.devices)d.dispose();this.devices.clear();}
}
class Device {
  constructor(abi){this.abi=abi;this.id=call('device');this.backend='wgpu-native/Vulkan';this.resources=new Set();this.disposed=false;this.white=this.texture(1,1,new Uint8Array([255,255,255,255]));this.gpu={queue:{writeTexture:(to,data)=>this.call('writeTexture',{id:to.texture.id},bytes(data))}};}
  live(){if(this.disposed)throw new Error('WebGPU device disposed');}
  call(op,args={},data=null){this.live();return call(op,{...args,device:this.id},data);}
  texture(width,height,data){const id=this.call('texture',{width,height},data),texture={id,owner:this,destroy:()=>{this.call('release',{id});this.resources.delete(texture);}};this.resources.add(texture);return texture;}
  async createShader(vertex,fragment){const s=new Shader(this,vertex,fragment);this.resources.add(s);return s;}
  createBuffer(values){const b=new Buffer(this,values);this.resources.add(b);return b;}
  createTarget(width,height){const t=new Target(this,width,height);this.resources.add(t);return t;}
  createSurface(canvas){return {present:async target=>{if(target.owner!==this||target.width!==canvas.width||target.height!==canvas.height)throw new Error('Surface size mismatch');this.call('present',{id:target.id});},dispose(){}};}
  async dispatch(source,buffer,groups){buffer.live();if(buffer.owner!==this||!Number.isInteger(groups)||groups<1||groups>65535)throw new Error('Invalid compute dispatch');this.call('dispatch',{id:buffer.id,source:safeWGSL(source),groups});}
  flush(){this.live();}
  async poll(){this.call('poll');}
  dispose(){if(this.disposed)return;this.call('disposeDevice');this.disposed=true;this.resources.clear();}
}
class Buffer {
  constructor(owner,values){this.owner=owner;this.length=values.length;this.id=owner.call('buffer',{},bytes(new Float32Array(values)));}
  live(){this.owner.live();if(!this.id)throw new Error('Buffer disposed');}
  write(values){this.live();if(values.length!==this.length||values.some(v=>!Number.isFinite(v)||!Number.isFinite(Math.fround(v))))throw new Error('Invalid buffer write');this.owner.call('writeBuffer',{id:this.id},bytes(new Float32Array(values)));}
  async read(){this.live();const data=decode64(native.gpuRead(this.owner.id,this.id,false));return Array.from(new Float32Array(data.buffer));}
  dispose(){if(!this.id)return;this.owner.call('release',{id:this.id});this.id=0;this.owner.resources.delete(this);}
}
class Shader {
  constructor(owner,vertex,fragment){this.owner=owner;this.fields=uniformLayout(vertex+'\n'+fragment);this.bytes=new Uint8Array(1024);this.id=owner.call('shader',{vertex:safeWGSL(owner.abi.header+vertex),fragment:safeWGSL(owner.abi.header+fragment)});this.disposed=false;}
  set(name,values,integer=false){this.owner.live();if(this.disposed)throw new Error('Shader disposed');const field=this.fields.get(name);values=Array.isArray(values)?values:[values];if(!field||values.length!==field.count||integer!==['i32','u32'].includes(field.type)||values.some(v=>!Number.isFinite(v))||integer&&values.some(v=>!Number.isInteger(v)||field.type==='u32'&&v<0))throw new Error('Invalid shader uniform');const view=new DataView(this.bytes.buffer);values.forEach((v,i)=>integer?view.setInt32(field.offset+i*4,v,true):view.setFloat32(field.offset+i*4,v,true));}
  dispose(){if(this.disposed)return;this.owner.call('release',{id:this.id});this.disposed=true;this.owner.resources.delete(this);}
}
class Target {
  constructor(owner,width,height){this.owner=owner;this.width=width;this.height=height;this.id=owner.call('target',{width,height});this.color={id:this.id,owner};this.disposed=false;}
  live(){this.owner.live();if(this.disposed)throw new Error('Target disposed');}
  clear(color){this.live();this.owner.call('clear',{id:this.id,color});}
  draw(shader,vertices,uniforms,depth=false,texture=null,blend=0,mode=4,clip=null){this.live();if(shader.owner!==this.owner||shader.disposed||texture&&texture.owner!==this.owner)throw new Error('Foreign GPU resource');const vertex=bytes(new Float32Array(vertices)),uniform=bytes(uniforms??defaultUniforms(this.owner.abi)),data=new Uint8Array(vertex.length+uniform.length+1024);data.set(vertex);data.set(uniform,vertex.length);data.set(shader.bytes,vertex.length+uniform.length);const bound=(v,max)=>Math.trunc(Math.max(0,Math.min(max,v))),x=clip?bound(clip[0],this.width):0,y=clip?bound(clip[1],this.height):0,right=clip?bound(clip[0]+clip[2],this.width):this.width,bottom=clip?bound(clip[1]+clip[3],this.height):this.height;this.owner.call('draw',{id:this.id,shader:shader.id,vertexBytes:vertex.length,depth,texture:(texture??this.owner.white).id,blend,mode,clip:[x,y,Math.max(0,right-x),Math.max(0,bottom-y)]},data);}
  flush(){this.live();}
  async readPixels(){this.live();return decode64(native.gpuRead(this.owner.id,this.id,true));}
  dispose(){if(this.disposed)return;this.owner.call('release',{id:this.id});this.disposed=true;this.owner.resources.delete(this);}
}
