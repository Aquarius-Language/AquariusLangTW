import test from 'node:test';
import assert from 'node:assert/strict';
import {storageLayout,textureLayout} from '../../AquariusCore/hosting/wgpu-textures.mjs';
import {glfwKey} from '../browser/wgpu-window.mjs';
test('storage buffers bind read-only at group 2 bindings 9..15 beside named textures',()=>{
  const source='@group(2) @binding(9) var<storage, read> quads: array<f32>; @binding(10) @group(2) var<storage> lights: array<vec4<f32>>; @group(2) @binding(0) var atlas: texture_2d<f32>; @group(2) @binding(8) var s: sampler;';
  assert.deepEqual([...storageLayout(source)],[['quads',9],['lights',10]]);
  assert.deepEqual(textureLayout(source).get('atlas'),{binding:0,dimension:'2d'});
  assert.equal(storageLayout('@group(1) @binding(0) var<uniform> user: UserUniforms;').size,0);
  assert.throws(()=>storageLayout('@group(2) @binding(3) var<storage, read> a: array<f32>;'));
  assert.throws(()=>storageLayout('@group(2) @binding(9) var<storage, read_write> a: array<f32>;'));
  assert.throws(()=>storageLayout('@group(2) @binding(9) var<storage> a: array<f32>; @group(2) @binding(9) var<storage> b: array<f32>;'));
});
test('DOM key codes map to GLFW numbering shared with the desktop host',()=>{
  assert.equal(glfwKey('KeyW'),87);assert.equal(glfwKey('KeyA'),65);assert.equal(glfwKey('Digit1'),49);assert.equal(glfwKey('Digit0'),48);
  assert.equal(glfwKey('Space'),32);assert.equal(glfwKey('ShiftLeft'),340);assert.equal(glfwKey('Escape'),256);assert.equal(glfwKey('F3'),292);
  assert.equal(glfwKey('Numpad5'),325);assert.equal(glfwKey('ArrowUp'),265);assert.equal(glfwKey('Unidentified'),-1);
});
