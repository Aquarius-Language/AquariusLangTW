// Executes the core compiler's stack instructions. Host calls may suspend for GPU
// readback and animation frames; Aquarius closures still use explicit VM frames.
export const num = (value, type = 'double') => ({type, value: type === 'int' ? (value>=-2147483648&&value<2147483648?Math.trunc(value):-2147483648) : type === 'float' ? Math.fround(value) : value});
export const numeric = v => v && ['int','float','double'].includes(v.type);
export const unwrap = v => numeric(v) ? v.value : Array.isArray(v) ? v.map(unwrap) : v;
export const wrap = v => typeof v === 'number' ? num(v) : Array.isArray(v) || ArrayBuffer.isView(v) ? Array.from(v, wrap) : v;
export class Scope {
  constructor(outer = null) { this.outer = outer; this.store = new Map(); this.owned = new Set(); }
  create(n,v) { this.store.set(n,v); this.owned.add(n); }
  get(n) { for(let s=this;s;s=s.outer) if(s.store.has(n)) return s.store.get(n); throw new Error(`Identifier not found: ${n}`); }
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
const truthy = v => v !== null && v !== false; // core treats all numbers as true
const key = v => { if(numeric(v)) return `${v.type}:${v.value}`; if(typeof v==='string'||typeof v==='boolean') return `${typeof v}:${v}`; throw new Error('Unusable as hash key'); };
const clone = c => {
  switch(c.type) { case 'int': case 'float': case 'double': return num(c.value,c.type); case 'bool': case 'string': case 'name': return c.value; case 'null': return null; default:return c; }
};
function binary(op,a,b,compound=false) {
  if(numeric(a)&&numeric(b)) {
    const x=a.value,y=b.value,t=compound?a.type:[a.type,b.type].includes('double')?'double':[a.type,b.type].includes('float')?'float':'int';
    switch(op) {
      case 'Add':return num(x+y,t); case 'Subtract':return num(x-y,t); case 'Multiply':return num(x*y,t); case 'Divide':return num(x/y,t);
      case 'Less':return x<y; case 'Greater':return x>y; case 'LessEqual':return x<=y; case 'GreaterEqual':return x>=y;
      case 'Equal':return x===y; case 'NotEqual':return x!==y;
    }
  }
  if(op==='Equal') return a===b; if(op==='NotEqual') return a!==b;
  if((op==='And'||op==='Or')&&typeof a==='boolean'&&typeof b==='boolean') return op==='And'?a&&b:a||b;
  if(op==='Add'&&typeof a==='string'&&typeof b==='string') return a+b;
  throw new Error(`Type mismatch for ${op}`);
}
export class VirtualMachine {
  constructor(host) { this.host=host; this.cancelled=false; this.instructions=0; }
  async invoke(fn,args=[]) {
    if(typeof fn==='function') return await fn(...args);
    if(fn?.type!=='closure') throw new Error('Not a function');
    if(args.length!==fn.parameters.length) throw new Error(`Function expects ${fn.parameters.length} arguments, got ${args.length}.`);
    const env=new Scope(fn.env);fn.parameters.forEach((p,i)=>env.create(p,args[i]));
    return this.execute(fn.program,env,fn.builtins);
  }
  async execute(program,env=new Scope(),builtins=this.host.builtins) {
    if(program.version!==1) throw new Error('Unsupported web bytecode version');
    const stack=[],frames=[];
    const frame=(p,e,b,base=stack.length)=>({p,e,b,base,ip:0,loops:[]});
    frames.push(frame(program,env,builtins,0));
    const args=n=>stack.splice(stack.length-n,n);
    const load=(f,n)=>{ try{return f.e.get(n);}catch(e){ if(f.b.has(n))return f.b.get(n);throw e; } };
    const call=async(f,fn,a)=>{
      if(typeof fn==='function') { stack.push(await fn(...a));return; }
      if(fn?.type!=='closure') throw new Error('Not a function');
      if(fn.parameters.length!==a.length) throw new Error(`Function expects ${fn.parameters.length} arguments, got ${a.length}.`);
      const scope=new Scope(fn.env);fn.parameters.forEach((p,i)=>scope.create(p,a[i]));frames.push(frame(fn.program,scope,fn.builtins));
    };
    while(frames.length) {
      if(this.cancelled||this.host.signal?.aborted) throw new Error('Execution cancelled');
      // Yield periodically even for non-graphics programs, so Stop and input work.
      if(++this.instructions % 20000===0 && typeof window!=='undefined') await new Promise(r=>setTimeout(r,0));
      const f=frames.at(-1);
      if(f.ip===f.p.code.length) { const result=stack.length>f.base?stack.pop():undefined;stack.length=f.base;frames.pop();if(!frames.length)return result;stack.push(result);continue; }
      const [op,n]=f.p.code[f.ip++],c=f.p.pool[n];
      switch(op) {
        case 'Constant':stack.push(clone(c));break;
        case 'Void':stack.push(undefined);break; case 'Null':stack.push(null);break;
        case 'Pop':stack.pop();break;case 'Duplicate':stack.push(stack.at(-1));break;
        case 'Load':stack.push(load(f,c.value));break;
        case 'Declare':f.e.create(c.value,stack.pop());stack.push(undefined);break;
        case 'Assign':{const v=stack.pop();stack.pop();f.e.set(c.value,v);stack.push(undefined);break;}
        case 'CompoundAssign':{const b=stack.pop();stack.pop();f.e.set(c.name,binary(c.operation,load(f,c.name),b,true));stack.push(undefined);break;}
        case 'Add':case 'Subtract':case 'Multiply':case 'Divide':case 'Less':case 'Greater':case 'LessEqual':case 'GreaterEqual':case 'Equal':case 'NotEqual':case 'And':case 'Or':{const b=stack.pop();stack.push(binary(op,stack.pop(),b));break;}
        case 'Not':{const v=stack.pop();stack.push(typeof v==='boolean'?!v:v===null);break;}
        case 'Negate':{const v=stack.pop();if(!numeric(v))throw new Error('Negate requires a number');stack.push(num(-v.value,v.type));break;}
        case 'IncrementPrefix':case 'IncrementPostfix':{const v=f.e.get(c.value);if(!numeric(v))throw new Error('Increment requires a number');const next=num(v.value+1,v.type);f.e.set(c.value,next);stack.push(op==='IncrementPrefix'?next:v);break;}
        case 'Jump':f.ip=n;break;case 'JumpIfFalse':if(!truthy(stack.pop()))f.ip=n;break;
        case 'JumpIfBreak':if(stack.at(-1)?.type==='break')f.ip=n;break;
        case 'Closure':stack.push({...c,type:'closure',env:f.e,builtins:f.b});break;
        case 'Call':{const a=args(n),fn=stack.pop();await call(f,fn,a);break;}
        case 'Return':{const r=stack.pop();stack.length=f.base;frames.pop();if(!frames.length)return r;stack.push(r);break;}
        case 'Array':stack.push(args(n));break;
        case 'CheckHashKey':key(stack.at(-1));break;
        case 'Hash':{const a=args(n*2),h=new Map();for(let i=0;i<a.length;i+=2)h.set(key(a[i]),[a[i],a[i+1]]);stack.push(h);break;}
        case 'Index':{const i=stack.pop(),a=stack.pop();if(Array.isArray(a)&&i?.type==='int')stack.push(a[i.value]??null);else if(a instanceof Map)stack.push(a.get(key(i))?.[1]??null);else throw new Error('Index operator not supported');break;}
        case 'CheckArrayWrite':if(!Array.isArray(stack.at(-2))||stack.at(-1)?.type!=='int'||stack.at(-1).value<0||stack.at(-1).value>=stack.at(-2).length)throw new Error('Array assignment index is out of bounds or invalid');break;
        case 'WriteIndex':{const v=stack.pop(),i=stack.pop(),a=stack.pop();if(!Array.isArray(a)||i?.type!=='int'||i.value<0||i.value>=a.length)throw new Error('Array assignment index is out of bounds');a[i.value]=v;stack.push(v);break;}
        case 'Member':case 'MemberFunction':{const m=stack.pop();if(m?.type!=='module')throw new Error('Cannot access member');stack.push(m.scope.get(c.value));break;}
        case 'ResolveMemberFunction':{const m=stack.pop();if(m?.type!=='module')throw new Error('Cannot access member');frames.push(frame(c.program,m.scope,f.b));break;}
        case 'EnterLoop':f.loops.push({outer:f.e,base:stack.length,binding:n<0?null:c.value});f.e=new Scope(f.e);break;
        case 'LoopCondition':{const v=stack.pop();if(typeof v!=='boolean')throw new Error('Expected bool from for loop conditionals');if(!v)f.ip=n;break;}
        case 'NextIteration':{const l=f.loops.at(-1),v=l.binding===null?null:f.e.get(l.binding);f.e=new Scope(l.outer);if(l.binding!==null)f.e.create(l.binding,v);break;}
        case 'Break':stack.length=f.loops.at(-1).base;f.ip=n;break;
        case 'LeaveLoop':{const l=f.loops.pop();stack.length=l.base;f.e=l.outer;stack.push(undefined);break;}
        case 'Error':throw new Error(c.value);
        default:throw new Error(`Invalid opcode: ${op}`);
      }
    }
  }
}
