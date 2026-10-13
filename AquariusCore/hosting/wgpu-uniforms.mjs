import {mat4} from './vendor-gl-matrix.mjs';
export function defaultUniforms(abi){const u=new Float32Array(abi.uniformBytes/4);for(let i=0;i<4;i++)u.set(mat4.create(),i*16);return u;}
