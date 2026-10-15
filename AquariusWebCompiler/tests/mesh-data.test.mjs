import test from 'node:test';
import assert from 'node:assert/strict';
import {decodeMeshData} from '../../AquariusCore/hosting/mesh-data.mjs';
import {Processing} from '../../AquariusCore/hosting/processing.mjs';
import {Scope,module,wrap,unwrap} from '../../AquariusCore/hosting/values.mjs';
function mesh(){const bytes=new Uint8Array(112),view=new DataView(bytes.buffer);[0x314d5141,3,8,0].forEach((v,i)=>view.setUint32(i*4,v,true));
  [[0,0,0,0,0,1,0,0],[2,0,0,0,0,1,1,0],[0,2,0,0,0,1,0,1]].flat().forEach((v,i)=>view.setFloat32(16+i*4,v,true));return bytes;}
test('AQM1 preserves triangle order, normals, UVs and nonzero buffer offsets',()=>{
  const bytes=mesh(),padded=new Uint8Array(bytes.length+3);padded.set(bytes,3);const m=decodeMeshData(padded.subarray(3));
  assert.deepEqual(m.vertices,[[0,0,0],[2,0,0],[0,2,0]]);assert.deepEqual(m.attributes[1],{normal:[0,0,1],uv:[1,0]});
});
test('malformed counts, format, truncated payloads and nonfinite/zero normals fail',()=>{
  for(const [offset,value]of [[0,0],[4,0],[4,2],[4,1000002],[8,9],[12,1]]){const bytes=mesh();new DataView(bytes.buffer).setUint32(offset,value,true);assert.throws(()=>decodeMeshData(bytes));}
  assert.throws(()=>decodeMeshData(mesh().subarray(0,111)));
  for(const [offset,value]of [[16,NaN],[20,Infinity],[36,0]]){const bytes=mesh();new DataView(bytes.buffer).setFloat32(offset,value,true);assert.throws(()=>decodeMeshData(bytes));}
});
test('bulk loader yields an editable retained shape outside beginDraw',()=>{
  const host={objects:new Map(),asset:()=>mesh(),newModule:()=>module(new Scope()),set:(m,n,v)=>m.scope.create(n,wrap(v)),bind(m,l,n,f){m.scope.create(n,(...a)=>f(...a.map(unwrap)));},installProcessingInput(){}};
  const p=new Processing(host);p.device={};const shape=p.module.scope.get('loadMesh')('mesh.aqmesh');
  assert.equal(unwrap(shape.scope.get('getVertexCount')()),3);assert.deepEqual(shape.scope.get('getVertex')(1),[2,0,0]);
  shape.scope.get('setVertex')(1,4,0,0);assert.deepEqual(shape.scope.get('getVertex')(1),[4,0,0]);assert.equal(host.objects.get(shape).style.hasStroke,false);
});
