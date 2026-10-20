import {Gunzip} from './vendor-fflate.mjs';
import {crc32} from './application-core.mjs';
export function textureDescriptor(width,height,format='rgba8unorm',dimension='2d',mipLevels=1) {
  if(!Number.isInteger(width)||!Number.isInteger(height)||width<1||height<1||width>8192||height>8192||
    !['2d','cube'].includes(dimension)||dimension==='cube'&&width!==height||
    !['r8unorm','rgba8unorm','rgba8unorm-srgb','rgba16float'].includes(format)||!Number.isInteger(mipLevels)||
    mipLevels<1||mipLevels>1+Math.floor(Math.log2(Math.max(width,height))))throw new Error('Invalid texture descriptor');
  const layers=dimension==='cube'?6:1,bytesPerPixel=format==='rgba16float'?8:format==='r8unorm'?1:4;
  let byteLength=0;for(let mip=0;mip<mipLevels;mip++)byteLength+=Math.max(1,width>>mip)*Math.max(1,height>>mip)*layers*bytesPerPixel;
  if(byteLength>128*1024*1024)throw new Error('Texture data exceeds 128 MiB');
  return {width,height,format,dimension,mipLevels,layers,bytesPerPixel,byteLength};
}
export function decodeTextureData(descriptor,bytes) {
  if(bytes[0]===31&&bytes[1]===139){
    if(bytes.length<18||new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength).getUint32(bytes.length-4,true)!==descriptor.byteLength)throw new Error('Invalid compressed texture size');
    const expectedCrc=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength).getUint32(bytes.length-8,true);
    const output=new Uint8Array(descriptor.byteLength);let offset=0;
    const codec=new Gunzip(chunk=>{if(offset+chunk.length>output.length)throw new Error('Invalid compressed texture size');output.set(chunk,offset);offset+=chunk.length;});
    for(let i=0;i<bytes.length;i+=65536)codec.push(bytes.subarray(i,i+65536),i+65536>=bytes.length);
    if(offset!==output.length)throw new Error('Invalid compressed texture size');bytes=output;
    if(crc32(bytes)!==expectedCrc)throw new Error('Invalid compressed texture checksum');
  }
  validateTextureBytes(descriptor,bytes);return bytes;
}
// Browsers validate gzip CRC/trailer in native code. Bound every output chunk
// before copying, so a forged trailer cannot grow the decoded allocation.
export async function decodeTextureDataAsync(descriptor,bytes,signal) {
  signal?.throwIfAborted();
  if(bytes[0]!==31||bytes[1]!==139||typeof DecompressionStream!=='function')return decodeTextureData(descriptor,bytes);
  if(bytes.length<18||new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength).getUint32(bytes.length-4,true)!==descriptor.byteLength)throw new Error('Invalid compressed texture size');
  const reader=new Blob([bytes]).stream().pipeThrough(new DecompressionStream('gzip')).getReader();
  const output=new Uint8Array(descriptor.byteLength);let offset=0;
  const abort=()=>{reader.cancel(signal.reason).catch(()=>{});};signal?.addEventListener('abort',abort,{once:true});
  try {
    for(;;){signal?.throwIfAborted();const {value,done}=await reader.read();if(done)break;
      if(offset+value.length>output.length)throw new Error('Invalid compressed texture size');output.set(value,offset);offset+=value.length;
    }
    signal?.throwIfAborted();if(offset!==output.length)throw new Error('Invalid compressed texture size');
    validateTextureBytes(descriptor,output);return output;
  } finally {signal?.removeEventListener('abort',abort);await reader.cancel().catch(()=>{});reader.releaseLock();}
}
export function validateTextureBytes(descriptor,bytes) {
  if(!(bytes instanceof Uint8Array)||bytes.length!==descriptor.byteLength)throw new Error('Texture data length does not match its descriptor');
  if(descriptor.format==='rgba16float')for(let i=1;i<bytes.length;i+=2)if((bytes[i]&0x7c)===0x7c)throw new Error('Texture components must be finite half floats');
}
export function textureLayout(source) {
  source=source.replace(/\/\*[\s\S]*?\*\/|\/\/[^\r\n]*/g,'');
  const fields=new Map(),used=new Set();
  for(const m of source.matchAll(/@(group|binding)\s*\(\s*(\d+)\s*\)\s*@(group|binding)\s*\(\s*(\d+)\s*\)\s*var\s+([\p{L}\p{N}_]+)\s*:\s*([^;]+);/gu)) {
    if(m[1]===m[3]||Number(m[m[1]==='group'?2:4])!==2)continue;
    const binding=Number(m[m[1]==='binding'?2:4]),type=m[6].replace(/\s+/g,'');
    if(used.has(binding))throw new Error('Duplicate texture group binding');used.add(binding);
    if(binding===8&&type==='sampler')continue;
    if(binding>7||!['texture_2d<f32>','texture_cube<f32>'].includes(type))throw new Error('Group 2 supports 2D/cube float textures at 0..7 and a sampler at 8');
    if(fields.has(m[5]))throw new Error('Duplicate texture name');fields.set(m[5],{binding,dimension:type==='texture_cube<f32>'?'cube':'2d'});
  }
  return fields;
}
// Group 2 bindings 9..15 hold read-only storage buffers (`var<storage, read>`),
// shared by vertex pulling and fragment lookups. Mirrors WgpuTextureLayout.Buffers.
export function storageLayout(source) {
  source=source.replace(/\/\*[\s\S]*?\*\/|\/\/[^\r\n]*/g,'');
  const fields=new Map(),used=new Set();
  for(const m of source.matchAll(/@(group|binding)\s*\(\s*(\d+)\s*\)\s*@(group|binding)\s*\(\s*(\d+)\s*\)\s*var\s*<([^>]*)>\s*([\p{L}\p{N}_]+)\s*:/gu)) {
    if(m[1]===m[3]||Number(m[m[1]==='group'?2:4])!==2)continue;
    const binding=Number(m[m[1]==='binding'?2:4]),space=m[5].replace(/\s+/g,'');
    if(binding<9||binding>15||!['storage','storage,read'].includes(space))throw new Error('Group 2 storage buffers must be var<storage, read> at bindings 9..15');
    if(used.has(binding))throw new Error('Duplicate texture group binding');used.add(binding);
    if(fields.has(m[6]))throw new Error('Duplicate storage buffer name');fields.set(m[6],binding);
  }
  return fields;
}
