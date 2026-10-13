import {BrowserWgpuBackend} from './wgpu.mjs';
import {BrowserJoltBackend} from './physics.mjs';
import {registerLegacy} from './legacy.mjs';
import {BrowserSurface} from './surface.mjs';
import {BrowserApplication} from './application.mjs';
import {registerAudio} from './audio.mjs';
import {registerWebcam} from './webcam.mjs';
import {registerOnnx} from './onnx.mjs';
import {registerMediaPipe} from './mediapipe.mjs';
import {CapabilityHost} from './capability-host.mjs';
import {installProcessingInput} from './processing-input.mjs';
export class BrowserHost extends CapabilityHost {
  constructor(bundle,options={}) {super(bundle,{...options,adapters:{platform:'web',graphics:abi=>new BrowserWgpuBackend(abi),physics:()=>new BrowserJoltBackend(),registerLegacy,register:host=>{host.application=new BrowserApplication(host);registerAudio(host);registerWebcam(host);registerOnnx(host);registerMediaPipe(host);}}});}
  screenSize(){return {width:window.innerWidth,height:window.innerHeight};}
  installProcessingInput(p){installProcessingInput(p);}
  async yieldExecution(){
    if(!this.taskChannel){this.taskChannel=new MessageChannel();this.taskWaiters=[];this.taskChannel.port1.onmessage=()=>this.taskWaiters.shift()?.();this.signal.addEventListener('abort',()=>this.cancelYielding(),{once:true});}
    await new Promise(resolve=>{this.taskWaiters.push(resolve);this.taskChannel.port2.postMessage(null);});
  }
  cancelYielding(){for(const resolve of this.taskWaiters??[])resolve();this.taskWaiters=[];this.taskChannel?.port1.close();this.taskChannel?.port2.close();this.taskChannel=null;}
  async dispose(){this.cancelYielding();await super.dispose();}
  measureText(text,font,size){const c=document.createElement('canvas').getContext('2d');c.font=`${size}px ${font}`;return c.measureText(text);}
  rasterText(text,style,density){const lines=text.split('\n'),width=Math.max(1,Math.ceil(Math.max(...lines.map(t=>this.measureText(t,style.font,style.textSize).width)))+4),height=Math.max(1,Math.ceil(lines.length*style.leading+4));const canvas=document.createElement('canvas');canvas.width=Math.ceil(width*density);canvas.height=Math.ceil(height*density);const ctx=canvas.getContext('2d');ctx.scale(density,density);ctx.font=`${style.textSize}px ${style.font}`;ctx.fillStyle=`rgba(${style.fill.slice(0,3).map(v=>Math.round(v*255)).join(',')},${style.fill[3]})`;ctx.textBaseline='top';lines.forEach((l,i)=>ctx.fillText(l,2,i*style.leading+2));return {width,height,pixelWidth:canvas.width,pixelHeight:canvas.height,bytes:ctx.getImageData(0,0,canvas.width,canvas.height).data};}
  async download(name,blob){this.downloads.set(name,blob);if(this.frameLimit)return;const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download=name.split('/').at(-1);document.body.append(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);}
  surface(w,h,title){return new BrowserSurface(document.getElementById('surfaces'),w,h,title);}
  async nextFrame(fps=60,start=performance.now()){await new Promise(r=>requestAnimationFrame(r));const remaining=1000/fps-(performance.now()-start);if(remaining>1)await new Promise(r=>setTimeout(r,remaining));}
  async imageBytes(path){const bytes=this.asset(path);if(path.endsWith('.ppm')){let pos=0;const token=()=>{while(pos<bytes.length){if(bytes[pos]===35){while(pos<bytes.length&&bytes[pos]!==10)pos++;}else if(bytes[pos]<=32)pos++;else break;}const start=pos;while(pos<bytes.length&&bytes[pos]>32)pos++;return new TextDecoder().decode(bytes.subarray(start,pos));};const format=token(),width=Number(token()),height=Number(token()),max=Number(token());if(!['P3','P6'].includes(format)||max>255||max<1)throw new Error('Unsupported PPM format');const out=new Uint8Array(width*height*4);if(format==='P6'){if(bytes[pos]===13&&bytes[pos+1]===10)pos+=2;else pos++;for(let i=0;i<width*height;i++)out.set([bytes[pos+i*3]*255/max,bytes[pos+i*3+1]*255/max,bytes[pos+i*3+2]*255/max,255],i*4);}else for(let i=0;i<width*height;i++)out.set([Number(token())*255/max,Number(token())*255/max,Number(token())*255/max,255],i*4);return {width,height,bytes:out};}const img=await createImageBitmap(new Blob([bytes])),c=document.createElement('canvas');c.width=img.width;c.height=img.height;const ctx=c.getContext('2d');ctx.drawImage(img,0,0);img.close();return {width:c.width,height:c.height,bytes:ctx.getImageData(0,0,c.width,c.height).data};}
  async saveImage(path,w,h,bytes){const c=document.createElement('canvas');c.width=w;c.height=h;c.getContext('2d').putImageData(new ImageData(new Uint8ClampedArray(bytes),w,h),0,0);const blob=await new Promise(r=>c.toBlob(r,'image/png'));this.downloads.set(path,blob);if(!this.frameLimit){const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download=path.split('/').at(-1);a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}}
}
