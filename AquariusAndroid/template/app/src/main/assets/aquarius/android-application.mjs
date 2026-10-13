import {AndroidWindow} from './android-window.mjs';
import {ApplicationRuntime} from './application-runtime.mjs';
import {VirtualFiles} from './virtual-files.mjs';
import {storageKey,checkBytes} from './application-core.mjs';
import {initializeCodec,inspectImage,decodeImage,encodeImage} from './codec-engine.mjs';
const nativeCall=(operation,key='',value=null)=>JSON.parse(native.storage(operation,key,JSON.stringify(value)));
class AndroidStorage {
  constructor(namespace){this.name='aquarius:'+namespace;this.available=true;}
  key(key){return this.name+'/'+key;}
  async get(key,signal){signal?.throwIfAborted();const value=nativeCall('get',this.key(key));if(value?.bytes)value.bytes=decode64(value.bytes);return value;}
  async put(key,value,signal){signal?.throwIfAborted();if(value.bytes)checkBytes(value.bytes.length);nativeCall('put',this.key(key),{...value,bytes:value.bytes?encode64(value.bytes):undefined});}
  async delete(key,signal){signal?.throwIfAborted();nativeCall('delete',this.key(key));}
  async keys(signal){signal?.throwIfAborted();return nativeCall('keys').filter(k=>k.startsWith(this.name+'/')).map(k=>k.slice(this.name.length+1));}
  async transfer(from,to,overwrite,move,signal){if(to===from||to.startsWith(from+'/')||from.startsWith(to+'/'))throw new Error('Conflict: Overlapping resources');const keys=(await this.keys(signal)).filter(k=>k===from||k.startsWith(from+'/'));const copy=await Promise.all(keys.map(async k=>[k,await this.get(k,signal)]));if(!copy.length)throw new Error('NotFound: Source resource missing');if(!overwrite&&(await this.keys(signal)).some(k=>k===to||k.startsWith(to+'/')))throw new Error('Conflict: Destination exists');for(const [k,v]of copy)await this.put(to+k.slice(from.length),v,signal);if(move)for(const [k]of copy)await this.delete(k,signal);}
  async read(key,signal){return (await this.get('settings/'+storageKey(key),signal))?.bytes??null;}
  async write(key,bytes,signal){return this.put('settings/'+storageKey(key),{bytes,modified:Date.now()},signal);}
  async remove(key,signal){return this.delete('settings/'+storageKey(key),signal);}
  async dispose(){}
}
class AndroidImages {
  async ready(signal){signal?.throwIfAborted();await initializeCodec(decode64(native.readAsset('vendor-magick.wasm')));signal?.throwIfAborted();}
  async inspect(data,signal){await this.ready(signal);return inspectImage(data);}
  async decode(data,frame,signal){await this.ready(signal);return decodeImage(data,frame);}
  async encode(image,format,options,signal){await this.ready(signal);return encodeImage(image,format,options);}
  dispose(){}
}
function documentHandle(descriptor){const call=(op,args={})=>JSON.parse(native.document(op,JSON.stringify({uri:descriptor.uri,...args})));return {...descriptor,async getFile(){const result=call('read');const blob=new Blob([decode64(result.bytes)]);blob.name=descriptor.name;blob.lastModified=result.modified??0;return blob;},async createWritable(){let data,aborted=false;return {async write(bytes){data=bytes.slice();},async close(){if(!aborted&&data)call('write',{bytes:encode64(data)});},async abort(){aborted=true;}};},async *entries(){for(const entry of call('enumerate').entries)yield [entry.name,documentHandle(entry)];},async remove(options){call('delete',options);}};}
export function registerApplication(host){
  const storage=new AndroidStorage(host.bundle.entry),images=new AndroidImages(),files=new VirtualFiles(storage,(name,blob)=>host.download(name,blob));
  const cap=available=>({available,restriction:null});
  const service=(operation,args={})=>JSON.parse(native.service(operation,JSON.stringify(args)));const clipboard={capabilities:{text:cap(true),image:cap(true),multiple:cap(true)},async formats(){const data=service('clipboardRead');return data.image?['text/plain','image/png']:['text/plain'];},async read(){const data=service('clipboardRead');return {text:data.text,image:data.image?await images.decode(decode64(data.image),0,host.signal):null};},async write(value){service('clipboardWrite',{text:value.text??'',...(value.image?{image:{...value.image,pixels:encode64(value.image.pixels)}}:{})});}};
  const fonts={async enumerate(){return JSON.parse(native.fonts());},available:family=>native.fontAvailable(family),measure(text,family,size){return {x:0,y:0,width:native.measureText(text,family,size),height:size};},selection(text,start,end,family,size){return [{x:native.measureText(text.slice(0,start),family,size),y:0,width:native.measureText(text.slice(start,end),family,size),height:size}];}};
  const select=async(operation,options)=>JSON.parse(native.picker(operation,JSON.stringify(options))).files.map(d=>files.register(documentHandle(d),d.name,d.kind==='directory'?'Directory':'File'));
  const services={launchFiles:[],capabilities:{files:cap(true),codecs:cap(true),persistentStorage:cap(true),pickers:cap(true),directoryPicker:cap(true),printing:cap(true),imageAcquisition:cap(true),fontEnumeration:cap(true),accessibility:cap(true),fileAssociations:cap(false)},openFiles:options=>select('open',options),async saveFile(options){return (await select('save',options))[0]??null;},async pickDirectory(){return (await select('directory',{}))[0]??null;},async printImage(image){service('print',{...image,pixels:encode64(image.pixels)});},async acquireImage(signal){const resources=await select('image',{});return resources.length?images.decode(await files.read(resources[0],signal),0,signal):null;},updateAccessibility(nodes){native.accessibility(JSON.stringify(nodes));},dispose(){}};
  const window=()=>new AndroidWindow(host,native);
  host.application=new ApplicationRuntime(host,{storage,images,files,clipboard,fonts,services,window});
}
