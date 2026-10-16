// Executed in the real Android JNI runtime by scripts/test-android.ps1.
export const aqua_library_abi = () => 1;
export function aqua_create_library() { return {async run() {
  const check = (condition, message) => { if (!condition) throw Error('Wasmtime contract: '+message); };
  const leb = n => { const result = []; do { let byte = n & 127; n >>>= 7; result.push(byte | (n ? 128 : 0)); } while (n); return result; };
  const string = value => { const data = [...new TextEncoder().encode(value)]; return [...leb(data.length), ...data]; };
  const section = (id, data) => [id, ...leb(data.length), ...data];
  const exp = (name, kind, index) => [...string(name), kind, index];
  const bodies = [[0,32,0,16,0,11], [0,65,1,64,0,11], [0,0,11], [0,66,42,11], [0,32,0,11]];
  const data = Uint8Array.from([0,97,115,109,1,0,0,0,
    ...section(0,[...string('test'),12,34,56]),
    ...section(1,[3,96,1,127,1,127,96,0,1,127,96,0,1,126]),
    ...section(2,[1,...string('env'),...string('callback'),0,0]),
    ...section(3,[5,0,1,1,2,0]), ...section(4,[1,112,1,1,2]), ...section(5,[1,1,1,3]),
    ...section(6,[1,127,1,65,7,11]),
    ...section(7,[8,...exp('memory',2,0),...exp('table',1,0),...exp('global',3,0),...exp('call',0,1),...exp('grow',0,2),...exp('trap',0,3),...exp('big',0,4),...exp('echo',0,5)]),
    ...section(9,[1,0,65,0,11,1,1]), ...section(10,[5,...bodies.flatMap(body=>[...leb(body.length),...body])])]);
  check(WebAssembly.validate(data), 'module validation');
  check(!WebAssembly.validate(Uint8Array.of(0,1,2)), 'malformed validation');
  const padded = new Uint8Array(data.length+4); padded.set(data,2);
  const module = await WebAssembly.compile(padded.subarray(2,-2));
  check(WebAssembly.Module.imports(module)[0].name === 'callback', 'import descriptors');
  check(WebAssembly.Module.exports(module).length === 8, 'export descriptors');
  check(new Uint8Array(WebAssembly.Module.customSections(module,'test')[0]).join() === '12,34,56', 'custom sections');
  let instance;
  instance = await WebAssembly.instantiate(module,{env:{callback:n=>instance.exports.echo(n)+1}});
  const e = instance.exports;
  check(e.call(41) === 42, 'reentrant host callback');
  check(e.big() === 42n, 'i64 BigInt');
  check(e.table.get(0) === e.call && e.table.get(0)(8) === 9, 'table function identity');
  check(e.table.grow(1) === 1 && e.table.get(1) === null, 'table growth');
  e.table.set(1,e.echo); check(e.table.get(1)(17) === 17, 'table set');
  e.global.value=13; check(e.global.value === 13, 'mutable globals');
  const old = e.memory.buffer; new Uint8Array(old)[7]=42;
  check(e.grow() === 1 && old.byteLength === 0, 'immediate guest memory view invalidation');
  check(e.memory.buffer.byteLength === 131072, 'guest memory growth');
  check(new Uint8Array(e.memory.buffer)[7] === 42, 'memory preservation');
  const previous=e.memory.buffer;
  check(e.memory.grow(1) === 2 && previous.byteLength === 0, 'host memory growth');
  let trapped=false; try { e.trap(); } catch { trapped=true; } check(trapped && e.echo(5) === 5, 'trap recovery');
  let forwarded=false;
  const throwing=await WebAssembly.instantiate(module,{env:{callback(){throw Error('callback-marker');}}});
  try { throwing.exports.call(0); } catch(e) { forwarded=String(e).includes('callback-marker'); } check(forwarded,'host exception propagation');
  const hostMemory=new WebAssembly.Memory({initial:1,maximum:2});
  const hostTable=new WebAssembly.Table({element:'anyfunc',initial:1,maximum:2});
  const hostGlobal=new WebAssembly.Global({value:'i32',mutable:true},12);
  check(hostMemory.buffer.byteLength === 65536 && hostTable.length === 1 && hostGlobal.value === 12, 'host resources');
  const alias=await import('./'+import.meta.url.split('/').at(-1));
  check(alias.aqua_create_library === aqua_create_library,'canonical module identity');
  return new TextEncoder().encode('WASMTIME_CONTRACT_OK');
}}; }
