import {registerDynamicLibrary,createWasmLibrary} from './dynamic-library-core.mjs';
export function registerAndroidDynamicLibrary(host, bridge) {
  const command = (operation, args) => JSON.parse(bridge.dynamicLibrary(operation, JSON.stringify(args)));
  registerDynamicLibrary(host, {
    platform:'android', isSupported:kind => ['native','so','wasm'].includes(kind),
    async load(path) {
      if (/\.wasm$/i.test(path)) return createWasmLibrary(host.asset(path));
      const {id} = command('open', {path}); let closed = false;
      const live = () => { if (closed) throw new Error('Library is closed / 函式庫已關閉'); };
      return {
        hasSymbol(symbol) { live(); return command('has', {id,symbol}).found; },
        invoke(symbol, input) { live(); return decode64(command('invoke', {id,symbol,input:encode64(input)}).output); },
        dispose() { if (closed) return; closed = true; command('dispose', {id}); }
      };
    }
  });
}
