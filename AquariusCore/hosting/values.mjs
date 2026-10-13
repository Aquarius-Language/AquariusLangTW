export const num = (value, type = 'double') => ({type, value: type === 'int' ? (value>=-2147483648&&value<2147483648?Math.trunc(value):-2147483648) : type === 'float' ? Math.fround(value) : value});
export const numeric = v => v && ['int','float','double'].includes(v.type);
export const unwrap = v => numeric(v) ? v.value : Array.isArray(v) ? v.map(unwrap) : v;
export const wrap = v => typeof v === 'number' ? num(v) : Array.isArray(v) || ArrayBuffer.isView(v) ? Array.from(v, wrap) : v;
// Hosts may expose a synchronous entry alongside their Promise-based public API.
// This symbol marks capability transport; language execution stays in Wasm.
export const nativeCall = Symbol('Aquarius native call');
export const nativeOwner = Symbol('Aquarius native owner');
export const portableBuiltin = Symbol('Aquarius portable builtin');
export const isPromiseLike = value => value != null && (typeof value === 'object' || typeof value === 'function') && typeof value.then === 'function';
export class Scope {
  constructor(outer = null) { this.outer = outer; this.store = new Map(); this.owned = new Set(); }
  create(n,v) { this.store.set(n,v); this.owned.add(n); }
  // Return the binding scope so a stored undefined value is distinct from absence.
  resolve(n) { for(let s=this;s;s=s.outer) if(s.store.has(n)) return s; return null; }
  get(n) { const s=this.resolve(n); if(s)return s.store.get(n); throw new Error(`Identifier not found: ${n}`); }
  set(n,v) { let s=this; while(s && !s.owned.has(n)) { s.store.set(n,v); s=s.outer; } if(!s) throw new Error(`Identifier not found: ${n}`); s.store.set(n,v); }
}
export const module = scope => ({type:'module', scope});
export function inspect(v) {
  if(v === undefined) return ''; if(v === null) return 'null'; if(v === true) return '真'; if(v === false) return '假';
  if(numeric(v)){if(v.type==='float'&&Number.isFinite(v.value)){for(let digits=1;digits<=9;digits++){const n=Number(v.value.toPrecision(digits));if(Math.fround(n)===v.value)return String(n);}}return String(v.value);}
  if(Array.isArray(v)) return `[${v.map(inspect).join(', ')}]`;
  if(v instanceof Map) return `{${[...v.values()].map(p=>`${inspect(p[0])}: ${inspect(p[1])}`).join(', ')}}`;
  if(v?.type === 'module') return 'module'; if(v?.type === 'closure') return 'function'; return String(v);
}
