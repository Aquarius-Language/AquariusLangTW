import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {GeometryKernel,STATE,RANGE} from '../../AquariusCore/hosting/geometry-core.mjs';
import {Processing} from '../browser/processing.mjs';
import {Scope,module,wrap,unwrap} from '../browser/values.mjs';

const kernel=await GeometryKernel.create(new WebAssembly.Module(readFileSync(new URL('../../AquariusCore/graphics/geometry.wasm',import.meta.url))));
const square=(x,y,s)=>[[x,y,0],[x+s,y,0],[x+s,y+s,0],[x,y+s,0]];
function fillOnly(){const st=kernel.state;st.fill(0);st[STATE.hasFill]=1;st.set([1,0,0,1],STATE.fill);st[STATE.detail]=32;kernel.stateChanged();}
function input(points,extra=[]){const f=kernel.input(points.length*3+extra.length);points.forEach((p,i)=>f.set(p,i*3));f.set(extra,points.length*3);}
const area=(a,b,c)=>Math.abs((b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]))/2;
function fillTriangles(){const v=kernel.vertices(),out=[];for(const r of kernel.ranges())if(r.kind===RANGE.fill)for(let i=r.start;i<r.start+r.count;i+=3)out.push([0,1,2].map(j=>Array.from(v.subarray((i+j)*12,(i+j)*12+3))));return out;}
const inside=(t,p)=>{const s=(a,b)=>(b[0]-a[0])*(p[1]-a[1])-(b[1]-a[1])*(p[0]-a[0]),d=[s(t[0],t[1]),s(t[1],t[2]),s(t[2],t[0])];return d.every(x=>x>0)||d.every(x=>x<0);};

test('concave polygons triangulate in both windings and on vertical planes',()=>{
  const points=[[0,0,0],[4,0,0],[4,4,0],[2,2,0],[0,4,0]];
  for(const p of [points,[...points].reverse(),points.map(([x,y])=>[0,x,y])]){
    fillOnly();input(p);const ids=kernel.indices(kernel.run('geo_shape_indices',9,p.length));assert.equal(ids.length,9);
    let total=0;for(let i=0;i<9;i+=3){const [a,b,c]=[ids[i],ids[i+1],ids[i+2]].map(k=>p[k]),u=b.map((x,j)=>x-a[j]),w=c.map((x,j)=>x-a[j]);total+=Math.hypot(u[1]*w[2]-u[2]*w[1],u[2]*w[0]-u[0]*w[2],u[0]*w[1]-u[1]*w[0])/2;}
    assert.ok(Math.abs(total-12)<1e-4,String(total));
  }
});

test('holes are cut out of the fill for every combination of windings and hole count',()=>{
  const outer=square(0,0,10),holes=[square(2,2,3),square(6,5,2)];
  for(const reverseOuter of [false,true])for(const reverseHoles of [false,true])for(const count of [1,2]){
    const o=reverseOuter?[...outer].reverse():outer,h=holes.slice(0,count).map(x=>reverseHoles?[...x].reverse():x);
    fillOnly();input([...o,...h.flat()],h.map((_,i)=>4+i*4));kernel.run('geo_polygon_holes',4+count*4,count,1,1);
    const triangles=fillTriangles(),total=triangles.reduce((s,t)=>s+area(...t),0);
    assert.ok(Math.abs(total-(100-9-(count>1?4:0)))<1e-3,`${reverseOuter} ${reverseHoles} ${count}: ${total}`);
    for(const centre of [[3.5,3.5],...(count>1?[[7,6]]:[])])assert.ok(!triangles.some(t=>inside(t,centre)),'a hole was filled');
    assert.ok(triangles.some(t=>inside(t,[1,0.37]))&&triangles.some(t=>inside(t,[9.3,8.1])),'the boundary interior was not filled');
  }
});

test('aligned grids of holes and curved holes keep the exact remaining area',()=>{
  const grid=[];for(let r=0;r<3;r++)for(let c=0;c<3;c++)grid.push(square(2+c*10,2+r*10,5));
  const circle=Array.from({length:48},(_,i)=>[15+6*Math.cos(i*Math.PI/24),15+6*Math.sin(i*Math.PI/24),0]);
  const circleArea=48*36*Math.sin(Math.PI/24)/2;
  for(const [holes,expected] of [[grid,900-9*25],[[circle],900-circleArea],[[[...circle].reverse(),square(25,25,3)],900-circleArea-9]]){
    let start=4;const starts=holes.map(h=>{const s=start;start+=h.length;return s;});
    fillOnly();input([...square(0,0,30),...holes.flat()],starts);kernel.run('geo_polygon_holes',start,holes.length,1,1);
    const total=fillTriangles().reduce((s,t)=>s+area(...t),0);
    assert.ok(Math.abs(total-expected)<1e-2,`${holes.length} holes: ${total} vs ${expected}`);
  }
});

test('a hole outside the boundary is rejected with the polygon error',()=>{
  fillOnly();input([...square(0,0,4),...square(10,10,2)],[4]);
  assert.throws(()=>kernel.run('geo_polygon_holes',8,1,1,1),/Polygon boundary must be simple/);
});

test('Processing contours draw the kernel fill with the hole removed and stroke every ring',()=>{
  const draws=[];
  const host={objects:new Map(),bundle:{graphics:{uniformBytes:1120}},newModule:()=>module(new Scope()),set:(m,n,v)=>m.scope.create(n,wrap(v)),
    bind(m,library,name,fn,raw=false){m.scope.create(name,(...args)=>fn(...(raw?args:args.map(unwrap))));},installProcessingInput(){}};
  const p=new Processing(host);p.geometry=kernel;p.shader={};
  p.device={createTarget:()=>({color:{},clear(){},draw(shader,vertices){draws.push(Float32Array.from(vertices));},flush(){},dispose(){}})};
  const canvas=p.module.scope.get('createGraphics')(64,64),call=(name,...args)=>canvas.scope.get(name)(...args);
  call('beginDraw');call('fill',255,0,0);call('stroke',0,0,255);
  call('beginShape');for(const [x,y] of square(0,0,40))call('vertex',x,y);
  call('beginContour');for(const [x,y] of square(10,10,10))call('vertex',x,y);call('endContour');call('endShape',1);
  const red=[],blue=[];for(const d of draws)for(let i=0;i<d.length;i+=12)(d[i+8]===1&&d[i+10]===0?red:d[i+10]===1?blue:[]).push([d[i],d[i+1]]);
  let total=0;for(let i=0;i<red.length;i+=3)total+=area(red[i],red[i+1],red[i+2]);
  assert.ok(Math.abs(total-1500)<1e-3,String(total));
  assert.ok(blue.some(([x,y])=>Math.abs(x-10)<2&&Math.abs(y-10)<2)&&blue.some(([x,y])=>Math.abs(x-40)<2&&Math.abs(y-40)<2),'both rings are stroked');
});
