// Fetch binary assets with bounded concurrency, alongside Wasm compilation.
// The in-memory contract also accepts old bundles with inline base64 assets.
export async function prepareBundle(bundle, fetcher=fetch) {
  const checked=async path=>{const r=await fetcher(path);if(!r.ok)throw new Error(`Could not load ${path} (${r.status})`);return r;};
  const compile=(async()=>{
    const response=await checked(bundle.wasm);
    if(typeof WebAssembly.compileStreaming==='function'&&response.headers?.get('Content-Type')?.split(';')[0].trim()==='application/wasm')return WebAssembly.compileStreaming(response);
    return WebAssembly.compile(await response.arrayBuffer());
  })();
  const assets=(async()=>{
    if(bundle.assetEncoding===undefined)return;
    if(bundle.assetEncoding!=='files')throw new Error('Unsupported application asset encoding');
    const entries=Object.entries(bundle.assets);bundle.assetFiles=Object.fromEntries(entries);let next=0;
    await Promise.all(Array.from({length:Math.min(4,entries.length)},async()=>{
      while(next<entries.length){
        const [name,file]=entries[next++];
        if(typeof file!=='string'||!/^asset-[0-9a-f]{64}\.(bin|mjs)$/.test(file))throw new Error('Invalid application asset filename');
        bundle.assets[name]=new Uint8Array(await(await checked(file)).arrayBuffer());
      }
    }));
  })();
  const [module]=await Promise.all([compile,assets]);bundle.compiledModule=module;return bundle;
}
