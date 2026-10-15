import {num,unwrap} from './values.mjs';
export function registerTerminal(host, backend={platform:host.adapters.platform,isSupported:false}) {
  const m=host.library('Terminal');host.modules.set('終端機',m);
  host.set(m,'Platform',backend.platform);host.set(m,'平台',backend.platform);
  host.bind(m,'Terminal','IsSupported',()=>backend.isSupported);
  function text(v){if(typeof v!=='string'||v.includes('\0'))throw Error('Expected a string without NUL');return v;}
  async function run(executable,args,options=new Map()) {
    if(!backend.isSupported)throw Error('Terminal processes are unavailable on this platform');
    text(executable);if(!executable.trim()||!Array.isArray(args))throw Error('Invalid executable or arguments');args.forEach(text);
    if(!(options instanceof Map))throw Error('Options must be a map');
    const request={executable,args,timeoutMs:60000};
    for(const [key,rawValue] of options.values()){
      const value=unwrap(rawValue);
      if(key==='WorkingDirectory')request.workingDirectory=text(value);
      else if(key==='TimeoutMs'&&Number.isInteger(value)&&value>=1&&value<=3600000)request.timeoutMs=value;
      else throw Error('Invalid terminal option');
    }
    const r=await backend.run(request),record=host.newModule();
    for(const [en,zh,value]of [['ExitCode','結束代碼',num(r.exitCode,'int')],['StandardOutput','標準輸出',r.standardOutput],['StandardError','標準錯誤',r.standardError],['TimedOut','已逾時',r.timedOut]]){host.set(record,en,value);host.set(record,zh,value);}
    return record;
  }
  host.bind(m,'Terminal','Run',run);
  host.bind(m,'Terminal','RunScript',(script,options)=>run(backend.platform==='android'?'/system/bin/sh':'/bin/sh',['-c',text(script)],options));
}
