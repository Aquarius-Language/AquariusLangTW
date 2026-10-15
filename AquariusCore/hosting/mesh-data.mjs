// AQM1: 16-byte little-endian header; triangle-list float32 position/normal/UV.
export function decodeMeshData(bytes) {
  if(!(bytes instanceof Uint8Array)||bytes.length<16)throw new Error('Invalid mesh header');
  const view=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength),count=view.getUint32(4,true);
  if(view.getUint32(0,true)!==0x314d5141||view.getUint32(8,true)!==8||view.getUint32(12,true)!==0||count===0||count>1000000||count%3||bytes.length!==16+count*32)throw new Error('Invalid mesh header or length');
  const vertices=new Array(count),attributes=new Array(count);
  for(let i=0;i<count;i++){
    const v=new Array(8);for(let j=0;j<8;j++){v[j]=view.getFloat32(16+i*32+j*4,true);if(!Number.isFinite(v[j]))throw new Error('Mesh components must be finite');}
    if(Math.hypot(v[3],v[4],v[5])<1e-10)throw new Error('Mesh normal must be nonzero');
    vertices[i]=v.slice(0,3);attributes[i]={normal:v.slice(3,6),uv:v.slice(6,8)};
  }
  return {vertices,attributes};
}
