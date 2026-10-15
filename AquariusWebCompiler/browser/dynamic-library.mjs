import {registerDynamicLibrary,createWasmLibrary,createJavaScriptLibrary} from './dynamic-library-core.mjs';
import {lookupPath} from './package-paths.mjs';
export function registerBrowserDynamicLibrary(host) {
  registerDynamicLibrary(host, {
    platform:'web', isSupported:kind => ['wasm','js','mjs'].includes(kind),
    async load(path) {
      const name = lookupPath(host.bundle.assets, host.resolve(path));
      if (/\.wasm$/i.test(name)) return createWasmLibrary(host.asset(name));
      if (/\.(mjs|js)$/i.test(name)) {
        const file = host.bundle.assetFiles?.[name];
        if (typeof file !== 'string' || !/^asset-[0-9a-f]{64}\.mjs$/.test(file)) throw new Error('JavaScript libraries require exported module assets / JavaScript 函式庫需要匯出的模組資源');
        return createJavaScriptLibrary(await import(new URL(file, import.meta.url).href));
      }
      throw new Error('Unsupported web library format / 不支援的網頁函式庫格式');
    }
  });
}
