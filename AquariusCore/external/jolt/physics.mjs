export const number = n => {if(typeof n!=='number'||!Number.isFinite(n)||Math.abs(n)>1000000)throw new Error('Expected a finite number in -1000000..1000000');return n;};
export const vector = (v,count=3) => {if(!Array.isArray(v)||v.length!==count)throw new Error(`Expected a ${count}-element vector`);return v.map(number);};
export const mass = n => {number(n);if(n!==0&&n<.001)throw new Error('Mass must be 0 (static) or in 0.001..1000000 kilograms');return n;};
export const dimension = n => {number(n);if(n<.001||n>10000)throw new Error('Shape dimensions must be in 0.001..10000 meters');return n;};
export class JoltWorld {
  constructor(J,gravity){this.J=J;this.bodies=new Set();this.disposed=false;
    const settings=new J.JoltSettings(),filter=new J.ObjectLayerPairFilterTable(2);filter.EnableCollision(0,1);filter.EnableCollision(1,1);
    const broad=new J.BroadPhaseLayerInterfaceTable(2,2),l0=new J.BroadPhaseLayer(0),l1=new J.BroadPhaseLayer(1);broad.MapObjectToBroadPhaseLayer(0,l0);broad.MapObjectToBroadPhaseLayer(1,l1);J.destroy(l0);J.destroy(l1);
    settings.mObjectLayerPairFilter=filter;settings.mBroadPhaseLayerInterface=broad;settings.mObjectVsBroadPhaseLayerFilter=new J.ObjectVsBroadPhaseLayerFilterTable(broad,2,filter,2);
    // The pinned WASM build has a fixed temporary allocator. Native desktop
    // capacities exceed it during Step, even for an otherwise small scene.
    settings.mMaxBodies=4096;settings.mMaxBodyPairs=8192;settings.mMaxContactConstraints=4096;
    try{this.jolt=new J.JoltInterface(settings);this.system=this.jolt.GetPhysicsSystem();this.interface=this.system.GetBodyInterface();this.setGravity(gravity);}finally{J.destroy(settings);}
  }
  live(){if(this.disposed)throw new Error('Jolt world has been disposed');}
  body(b,dynamic=false){this.live();if(!this.bodies.has(b)||b.removed)throw new Error('Expected a live body from this Jolt world');if(dynamic&&!b.dynamic)throw new Error('Expected a dynamic body');return b.id;}
  temp(type,v,fn){const o=new this.J[type](...v);try{return fn(o);}finally{this.J.destroy(o);}}
  create(kind,size,position,m){this.live();vector(position);mass(m);if(this.bodies.size>=4096)throw new Error('Jolt world capacity is 4096 bodies');const J=this.J;
    let shape;if(kind==='sphere'){dimension(size);shape=new J.SphereShape(size,null);}else{vector(size).forEach(dimension);shape=this.temp('Vec3',size,h=>new J.BoxShape(h,Math.min(.05,Math.min(...size)*.1),null));}
    // BodyCreationSettings takes a reference to the shape; destroying it releases
    // that reference after the body has taken its own. No JS finalizers own bodies.
    let settings;
    try{return this.temp('RVec3',position,p=>this.temp('Quat',[0,0,0,1],q=>{
      settings=new J.BodyCreationSettings(shape,p,q,m>0?J.EMotionType_Dynamic:J.EMotionType_Static,m>0?1:0);
      settings.mLinearDamping=0;settings.mAngularDamping=0;settings.mFriction=.2;settings.mRestitution=0;settings.mMotionQuality=m>0?J.EMotionQuality_LinearCast:J.EMotionQuality_Discrete;
      if(m>0){settings.mOverrideMassProperties=J.EOverrideMassProperties_CalculateInertia;settings.mMassPropertiesOverride.mMass=m;}
      const native=this.interface.CreateBody(settings);if(!native)throw new Error('Jolt could not allocate a body');
      const id=new J.BodyID(native.GetID().GetIndexAndSequenceNumber());const b={type:'JOLT_BODY',id,dynamic:m>0,removed:false};this.interface.AddBody(id,m>0?J.EActivation_Activate:J.EActivation_DontActivate);this.bodies.add(b);return b;
    }));}finally{if(settings)J.destroy(settings);else J.destroy(shape);}
  }
  getGravity(){this.live();return this.read(this.system.GetGravity());}
  setGravity(v){this.live();vector(v);this.temp('Vec3',v,x=>this.system.SetGravity(x));for(const b of this.bodies)if(b.dynamic)this.interface.ActivateBody(b.id);}
  read(v,quaternion=false){try{const a=[v.GetX(),v.GetY(),v.GetZ()];if(quaternion)a.push(v.GetW());return a;}finally{this.J.destroy(v);}}
  get(name,b){const v=this.interface[name](this.body(b));return name==='IsActive'?v:this.read(v,name==='GetRotation');}
  set(name,b,v){const dynamic=['SetLinearVelocity','SetAngularVelocity','AddForce','AddImpulse','AddAngularImpulse'].includes(name),id=this.body(b,dynamic),J=this.J;
    if(name==='SetFriction'||name==='SetRestitution'){number(v);if(v<0||v>1)throw new Error('Expected a value in 0..1');this.interface[name](id,v);return;}
    vector(v,name==='SetRotation'?4:3);if(name==='SetRotation'){const length=Math.hypot(...v);if(length<1e-6)throw new Error('Quaternion must be nonzero');v=v.map(x=>x/length);}
    this.temp(name==='SetPosition'?'RVec3':name==='SetRotation'?'Quat':'Vec3',v,x=>this.interface[name](id,x,...(['SetPosition','SetRotation'].includes(name)?[J.EActivation_Activate]:[])));
    if(name==='SetAngularVelocity'||name==='AddForce')this.interface.ActivateBody(id);
  }
  step(dt,steps){this.live();number(dt);if(dt<.000001||dt>1||!Number.isInteger(steps)||steps<1||steps>128)throw new Error('Invalid physics step');const error=this.jolt.Step(dt,steps);if(error)throw new Error(`Jolt update failed: ${error}`);}
  remove(b){const id=this.body(b);this.interface.RemoveBody(id);this.interface.DestroyBody(id);this.J.destroy(id);b.removed=true;this.bodies.delete(b);}
  dispose(){if(this.disposed)return;for(const b of [...this.bodies])this.remove(b);this.J.destroy(this.jolt);this.disposed=true;}
}

