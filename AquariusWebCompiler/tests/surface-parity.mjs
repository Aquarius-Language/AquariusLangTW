import {readFileSync} from 'node:fs';
import {resizeSurfaceSize} from '../browser/surface.mjs';
const input=JSON.parse(readFileSync(process.argv[2],'utf8'));
let size=input.initial;
console.log(JSON.stringify(input.updates.map(dimensions=>{
  size=resizeSurfaceSize(size,...dimensions);
  return {...size,drawable:size.pixelWidth>0&&size.pixelHeight>0};
})));
