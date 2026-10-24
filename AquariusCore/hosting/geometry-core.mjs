// The Processing geometry kernel (geometry.wasm, built from AquariusCore/graphics/geometry.c), shared with the desktop
// host. Write `state` and the operation's input, run an operation, then draw `ranges()` of `vertices()` in order
// (12 floats per vertex: position, normal, uv, color). Mirrors AquariusCore/graphics/GeometryKernel.cs.
export const GEOMETRY_ABI=1;
export const STATE={weight:0,cap:1,join:2,is3D:3,width:4,height:5,stroke:6,fill:10,normal:14,hasFill:17,hasStroke:18,rectMode:19,ellipseMode:20,detail:21,tightness:22,mvp:23,size:39};
export const RANGE={fill:0,unlitFill:1,stroke:2};
const ERRORS={[-1]:'Polygon has zero area.',[-2]:'Polygon boundary must be simple and have nonzero area; use beginContour() for holes.',[-3]:'Geometry kernel memory exhausted.'};
export class GeometryKernel{
  constructor(instance){
    this.exports=instance.exports;this.exports._initialize?.();
    if(this.exports.geo_abi()!==GEOMETRY_ABI)throw new Error('Unsupported geometry kernel ABI');
    this.statePointer=this.exports.geo_state();
  }
  static async create(module){return new GeometryKernel(await WebAssembly.instantiate(module,{}));}
  floats(pointer,count){return new Float32Array(this.exports.memory.buffer,pointer,count);}
  // The state block; writes take effect at the next operation. stateChanged() drops the cached inverse transform.
  get state(){return this.floats(this.statePointer,STATE.size);}
  stateChanged(){this.exports.geo_state();}
  input(count){const p=this.exports.geo_input(Math.max(1,count));if(!p)throw new Error(ERRORS[-3]);return this.floats(p,count);}
  run(operation,...args){const result=this.exports[operation](...args);if(result<0)throw new Error(ERRORS[result]??`Geometry kernel error ${result}`);return result;}
  vertices(){const n=this.exports.geo_vertex_count();return n?this.floats(this.exports.geo_vertices(),n*12):new Float32Array(0);}
  // [{start,count,kind}] in vertex units.
  ranges(){const n=this.exports.geo_range_count(),view=new Int32Array(this.exports.memory.buffer,this.exports.geo_ranges(),n*3),out=[];for(let i=0;i<n;i++)out.push({start:view[i*3],count:view[i*3+1],kind:view[i*3+2]});return out;}
  indices(count){return count?new Int32Array(this.exports.memory.buffer,this.exports.geo_indices(),count):new Int32Array(0);}
  output(offset,count){return this.input(offset+count).subarray(offset);}
}
