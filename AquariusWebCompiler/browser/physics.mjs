import initJolt from './vendor-jolt.mjs';
import {JoltWorld,vector} from './physics-core.mjs';
export {number,vector,mass,dimension,JoltWorld} from './physics-core.mjs';
let initialized;
export class BrowserJoltBackend {
  constructor(){this.name='Jolt Physics';this.worlds=new Set();}
  async createWorld(gravity){vector(gravity);initialized??=initJolt({locateFile:()=>new URL('./vendor-jolt.wasm',import.meta.url).href});const J=await initialized;const world=new JoltWorld(J,gravity);this.worlds.add(world);return world;}
  dispose(){for(const w of this.worlds)w.dispose();this.worlds.clear();}
}