import test from 'node:test';
import assert from 'node:assert/strict';
import {gzipSync} from 'node:zlib';
import {textureDescriptor,validateTextureBytes,decodeTextureData,textureLayout} from '../../AquariusCore/hosting/wgpu-textures.mjs';
test('mip payload dimensions include cube layers and rectangular tails',()=>{
  assert.equal(textureDescriptor(4,4,'rgba16float','cube',3).byteLength,1008);
  assert.equal(textureDescriptor(4,2,'r8unorm','2d',3).byteLength,11);
  assert.throws(()=>textureDescriptor(4,2,'rgba16float','cube',1));
  assert.throws(()=>textureDescriptor(4,4,'rgba16float','2d',4));
  assert.throws(()=>textureDescriptor(8192,8192,'rgba16float','2d',1));
});
test('half floats and gzip output size are checked before GPU upload',()=>{
  const d=textureDescriptor(1,1,'rgba16float');
  assert.throws(()=>validateTextureBytes(d,new Uint8Array(7)));
  assert.throws(()=>validateTextureBytes(d,new Uint8Array([0,124,0,0,0,0,0,0])));
  assert.deepEqual(decodeTextureData(d,gzipSync(new Uint8Array(8))),new Uint8Array(8));
  assert.throws(()=>decodeTextureData(d,gzipSync(new Uint8Array(9))));
  const forged=gzipSync(new Uint8Array(9));forged.writeUInt32LE(8,forged.length-4);
  assert.throws(()=>decodeTextureData(d,forged));
});
test('named texture bindings validate dimensions, names and duplicate slots',()=>{
  const fields=textureLayout('@binding(7) @group(2) var normal:texture_2d<f32>; @group(2) @binding(0) var probe:texture_cube<f32>; @group(2) @binding(8) var s:sampler;');
  assert.deepEqual(fields.get('probe'),{binding:0,dimension:'cube'});
  assert.throws(()=>textureLayout('@group(2) @binding(0) var a:texture_2d<f32>; @group(2) @binding(0) var b:texture_2d<f32>;'));
  assert.throws(()=>textureLayout('@group(2) @binding(0) var a:texture_3d<f32>;'));
});
