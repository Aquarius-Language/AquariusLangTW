import test from 'node:test';
import assert from 'node:assert/strict';
import {runtime} from './wasm-fixture.mjs';
import {inspect} from '../browser/values.mjs';

const megabytes=vm=>vm.program.session.call('aqua_heap_bytes')/1048576;

test('freed large blocks are coalesced and split for later small allocations',async()=>{
  // 25 MiB of 1 MiB arrays, dropped, then 25 MiB of 16 KiB arrays (sized to fill their power-of-two blocks with the\n  // storage header): without coalescing and splitting the
  // second phase cannot reuse the first phase's blocks and the heap reaches at least 50 MiB.
  const {vm,index}=await runtime(`
    變數 large=Array.Make(0,0,32);迴圈(變數 i=0;i<25;i++){Array.Append(large,Array.Make(65535,0));}
    large=[];
    變數 small=Array.Make(0,0,2048);迴圈(變數 i=0;i<1600;i++){Array.Append(small,Array.Make(1023,0));}
    Array.Len(small);`);
  assert.equal(inspect(await vm.execute(index)),'1600');
  assert.ok(megabytes(vm)<42,`heap ${megabytes(vm).toFixed(1)} MiB`);
});

test('host-retained mirrors do not pin arrays the guest dropped, and stay usable as detached values',async()=>{
  const kept=[];
  const {vm,index}=await runtime(`
    迴圈(變數 i=0;i<300;i++){keep(Array.Make(8192,i));}
    變數 first=back();變數 copy=back();first[0]=99;[Array.Len(first),copy[0],first[0],first==copy];`,
    new Map([['keep',a=>{kept.push(a);return null;}],['back',()=>kept[0]]]));
  // 300 arrays of 128 KiB: about 38 MiB if host mirrors pinned them. The first array was dropped by the guest, so its
  // mirror is detached; returning it attaches one new guest array, and returning it again yields that same array.
  assert.equal(inspect(await vm.execute(index)),'[8192, 99, 99, 真]');
  assert.ok(megabytes(vm)<30,`heap ${megabytes(vm).toFixed(1)} MiB`);
  assert.equal(kept.length,300);assert.equal(kept[299].length,8192);
});
