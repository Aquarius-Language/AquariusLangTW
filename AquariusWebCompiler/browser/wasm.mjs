import {Scope,num,numeric,nativeCall,nativeOwner,isPromiseLike} from './values.mjs';
const truthy = v => v !== null && v !== false; // core treats all numbers as true
const key = v => { if(numeric(v)) return `${v.type}:${v.value}`; if(typeof v==='string'||typeof v==='boolean') return `${typeof v}:${v}`; throw new Error('Unusable as hash key'); };
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
// Each execution owns its imports and values. Program branches run inside native Wasm exports.
export class WasmRuntime {
  constructor(host) {this.host=host;this.cancelled=false;this.executions=new Set();this.nativeRoots=new Set();}
  // Host resource registries must not become language roots themselves. Include
  // suspended executions, operand values and borrowed native arguments instead.
  reachable(roots=[]) {
    const seen=new Set(),pending=[];
    const add=v=>{if(v&&(typeof v==='object'||typeof v==='function')&&!numeric(v)&&!seen.has(v)){seen.add(v);pending.push(v);}};
    for(const root of roots)add(root);
    for(const root of this.nativeRoots)add(root);
    for(const frames of this.executions)for(const f of frames){add(f.e);add(f.b);add(f.values);add(f.nativeValues);for(const loop of f.loops)add(loop.outer);}
    while(pending.length){const v=pending.pop();
      if(typeof v==='function'){add(v[nativeOwner]);continue;}
      if(v.type==='module')add(v.scope);
      else if(v.type==='closure'){add(v.env);add(v.builtins);}
      else if(v instanceof Scope){add(v.outer);for(const value of v.store.values())add(value);}
      else if(v instanceof Map){for(const [key,value] of v){add(key);add(value);}}
      else if(Array.isArray(v))for(const value of v)add(value);
    }
    return seen;
  }
  async ready() {
    if(this.program)return this.program;
    const module=this.host.bundle.compiledModule ?? await WebAssembly.compile(await (await fetch(this.host.bundle.wasm)).arrayBuffer());
    const sections=WebAssembly.Module.customSections(module,'aquarius.application');
    if(sections.length!==1)throw new Error('Missing or duplicate Aquarius metadata');
    const metadata=JSON.parse(new TextDecoder().decode(sections[0]));
    if(metadata.abiVersion!==1)throw new Error('Unsupported Aquarius WebAssembly ABI');
    this.program={module,metadata};return this.program;
  }
  async invoke(fn,args=[]) {
    if(typeof fn==='function'){const roots=[fn,...args];this.nativeRoots.add(roots);try{return await fn(...args);}finally{this.nativeRoots.delete(roots);}}
    if(fn?.type!=='closure')throw new Error('Not a function');
    if(args.length!==fn.parameters.length)throw new Error(`Function expects ${fn.parameters.length} arguments, got ${args.length}.`);
    const env=new Scope(fn.env);fn.parameters.forEach((p,i)=>env.create(p,args[i]));
    return this.execute(fn.function,env,fn.builtins,fn.program);
  }
  async execute(index,env=new Scope(),builtins=this.host.builtins,program=null) {
    program??=await this.ready();
    const frames=[],instances=new Map();let f,budget,child,pending;
    const frame=(p,id,e,b)=>({program:p,id,e,b,pc:0,values:[],loops:[]});
    frames.push(frame(program,index,env,builtins));
    this.executions.add(frames);
    const pop=()=>f.values.pop(),push=v=>f.values.push(v),peek=(n=1)=>f.values.at(-n),args=n=>f.values.splice(f.values.length-n,n),constant=n=>f.program.metadata.functions[f.id].pool[n];
    const load=n=>{const s=f.e.resolve(n);if(s)return s.store.get(n);if(f.b.has(n))return f.b.get(n);throw new Error(`Identifier not found: ${n}`);};
    const arrayCheck=()=>{if(!Array.isArray(peek(2))||peek()?.type!=='int'||peek().value<0||peek().value>=peek(2).length)throw new Error('Array assignment index is out of bounds or invalid');};
    const increment=(n,prefix)=>{const name=constant(n).text,v=f.e.get(name);if(!numeric(v))throw new Error('Increment requires a number');const next=num(v.value+1,v.type);f.e.set(name,next);push(prefix?next:v);};
    const imports={
      checkpoint:()=>{if(this.cancelled||this.host.signal?.aborted)throw new Error('Execution cancelled');return --budget<0?1:0;},
      constant:n=>{const c=constant(n);push(['int','float','double'].includes(c.type)?num(c.number,c.type):c.type==='bool'?c.boolean:c.type==='string'?c.text:c.type==='break'?{type:'break'}:null);},
      void:()=>push(undefined),null:()=>push(null),pop:()=>pop(),duplicate:()=>push(peek()),
      load:n=>push(load(constant(n).text)),declare:n=>{f.e.create(constant(n).text,pop());push(undefined);},
      assign:n=>{const v=pop();pop();f.e.set(constant(n).text,v);push(undefined);},
      compoundAssign:n=>{const c=constant(n),b=pop();pop();f.e.set(c.text,binary(c.operation,load(c.text),b,true));push(undefined);},
      not:()=>{const v=pop();push(typeof v==='boolean'?!v:v===null);},
      negate:()=>{const v=pop();if(!numeric(v))throw new Error('Negate requires a number');push(num(-v.value,v.type));},
      incrementPrefix:n=>increment(n,true),incrementPostfix:n=>increment(n,false),truth:()=>truthy(pop())?1:0,isBreak:()=>peek()?.type==='break'?1:0,
      closure:n=>{const c=constant(n);push({type:'closure',function:c.function,parameters:c.parameters,env:f.e,builtins:f.b,program:f.program});},
      call:n=>{const a=args(n),fn=pop();if(typeof fn==='function'){f.nativeValues=[fn,...a];const result=(fn[nativeCall]??fn)(...a);if(isPromiseLike(result)){const caller=f;pending=Promise.resolve(result).then(v=>caller.values.push(v));}else {push(result);f.nativeValues=null;}}
        else {if(fn?.type!=='closure')throw new Error('Not a function');if(fn.parameters.length!==n)throw new Error(`Function expects ${fn.parameters.length} arguments, got ${n}.`);const e=new Scope(fn.env);fn.parameters.forEach((p,i)=>e.create(p,a[i]));child=frame(fn.program,fn.function,e,fn.builtins);}},
      returned:()=>{},array:n=>push(args(n)),checkHashKey:()=>key(peek()),hash:n=>{const a=args(n*2),h=new Map();for(let i=0;i<a.length;i+=2)h.set(key(a[i]),[a[i],a[i+1]]);push(h);},
      index:()=>{const i=pop(),a=pop();if(Array.isArray(a)&&i?.type==='int')push(a[i.value]??null);else if(a instanceof Map)push(a.get(key(i))?.[1]??null);else throw new Error('Index operator not supported');},
      checkArrayWrite:arrayCheck,writeIndex:()=>{const v=pop();arrayCheck();const i=pop(),a=pop();a[i.value]=v;push(v);},
      member:n=>{const m=pop();if(m?.type!=='module')throw new Error('Cannot access member');push(m.scope.get(constant(n).text));},
      resolveMemberFunction:n=>{const m=pop();if(m?.type!=='module')throw new Error('Cannot access member');child=frame(f.program,constant(n).function,m.scope,f.b);},
      enterLoop:n=>{f.loops.push({outer:f.e,base:f.values.length,binding:n<0?null:constant(n).text});f.e=new Scope(f.e);},
      loopCondition:()=>{const v=pop();if(typeof v!=='boolean')throw new Error('Expected bool from for loop conditionals');return v?1:0;},
      nextIteration:()=>{const l=f.loops.at(-1),v=l.binding===null?null:f.e.get(l.binding);f.e=new Scope(l.outer);if(l.binding!==null)f.e.create(l.binding,v);},
      break:()=>{f.values.length=f.loops.at(-1).base;},leaveLoop:()=>{const l=f.loops.pop();f.values.length=l.base;f.e=l.outer;push(undefined);},error:n=>{throw new Error(constant(n).text);}
    };
    for(const op of ['Add','Subtract','Multiply','Divide','Less','Greater','LessEqual','GreaterEqual','Equal','NotEqual','And','Or'])imports[op[0].toLowerCase()+op.slice(1)]=()=>{const b=pop();push(binary(op,pop(),b));};
    try {while(frames.length){
      f=frames.at(-1);budget=4096;child=null;pending=null;
      let instance=instances.get(f.program);if(!instance){instance=new WebAssembly.Instance(f.program.module,{aquarius_v1:imports});instances.set(f.program,instance);}
      const next=instance.exports[`aqua_f${f.id}`](f.pc);
      if(pending){await pending;f.nativeValues=null;}
      if(next<0){const result=pop();frames.pop();if(!frames.length)return result;f=frames.at(-1);push(result);}
      else {f.pc=next;if(child)frames.push(child);}
      if(budget<0&&typeof window!=='undefined')await new Promise(r=>setTimeout(r,0));
    }}finally{this.executions.delete(frames);}
  }
}
