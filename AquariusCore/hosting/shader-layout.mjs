export function uniformLayout(source) {
  source=source.replace(/\/\*[\s\S]*?\*\/|\/\/[^\r\n]*/g,'');
  const body=source.match(/struct\s+UserUniforms\s*\{([^}]*)\}/)?.[1],fields=new Map();let offset=0;
  for(const text of body?.split(',').filter(x=>x.trim())??[]) {
    const m=text.match(/^\s*([\p{L}\p{N}_]+)\s*:\s*(f32|i32|u32|vec[234]<f32>|mat4x4<f32>)\s*$/u);
    if(!m)throw new Error('Unsupported UserUniforms field');
    const count=m[2].startsWith('mat')?16:m[2].startsWith('vec')?Number(m[2][3]):1,align=count===1?4:count===2?8:16;
    offset=Math.ceil(offset/align)*align;if(offset+count*4>1024||fields.has(m[1]))throw new Error('Duplicate or oversized UserUniforms field');
    fields.set(m[1],{offset,count,type:m[2]});offset+=count*4;
  }
  return fields;
}
export const safeWGSL = source => { const ids=new Map();return source.replace(/[\p{L}_][\p{L}\p{N}_]*/gu,s=>/[^\x00-\x7f]/.test(s)?(ids.has(s)?ids.get(s):(ids.set(s,`aqua_${ids.size}`),ids.get(s))):s); };
