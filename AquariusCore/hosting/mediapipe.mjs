import {num} from './values.mjs';

const count = m => m.shape.reduce((a,b)=>a*b,1);
const output = (s,n) => {const matches=s.outputs.filter(m=>m.type==='float32'&&count(m)===n);if(matches.length!==1)throw new Error('姿態模型的輸出介面不相容。');return matches[0].name;};
let code;
// Only bulk transport and ONNX calls belong here. The same pose.wasm performs
// detector decoding, affine resampling, projection and filtering on every host.
export class BrowserPoseTracker {
  static async create(backend, detectorBytes, landmarkBytes, provider, signal, wasmBytes) {
    const compiled=wasmBytes?await WebAssembly.compile(wasmBytes):await(code??=fetch(new URL('./pose.wasm',import.meta.url)).then(r=>{if(!r.ok)throw new Error('無法載入姿態處理程式。');return r.arrayBuffer();}).then(WebAssembly.compile).catch(e=>{code=null;throw e;}));
    const instance=await WebAssembly.instantiate(compiled), wasm=instance.exports;let d,l;
    try {
      if(wasm.pose_abi()!==1)throw new Error('姿態處理介面版本不相容。');
      d=await backend.createSession(detectorBytes,provider,signal);l=await backend.createSession(landmarkBytes,provider,signal);
      if(d.inputs.length!==1||l.inputs.length!==1||d.inputs[0].type!=='float32'||l.inputs[0].type!=='float32'||String(d.inputs[0].shape)!=='1,3,224,224'||String(l.inputs[0].shape)!=='1,256,256,3')throw new Error('必須使用相容的 MediaPipe 人物偵測與姿態模型。');
      return new BrowserPoseTracker(wasm,d,l,signal);
    }catch(e){await l?.dispose();await d?.dispose();throw e;}
  }
  constructor(wasm,d,l,signal){
    this.wasm=wasm;this.detector=d;this.landmark=l;this.signal=signal;
    this.boxes=output(d,2254*12);this.scores=output(d,2254);this.points=output(l,195);this.confidence=output(l,1);this.heatmap=output(l,64*64*39);
    this.disposed=false;this.running=null;this.wasm.pose_reset();
  }
  floats(slot,n){return new Float32Array(this.wasm.memory.buffer,this.wasm.pose_ptr(slot),n);}
  write(slot,tensor){this.floats(slot,tensor.data.length).set(tensor.data);}
  run(session,slot,outputs){const m=session.inputs[0];return session.run(new Map([[m.name,{type:'float32',shape:m.shape,data:this.floats(slot,count(m))}]]),outputs,this.signal);}
  live(){if(this.disposed||this.signal?.aborted)throw new Error('姿態追蹤器已關閉或取消。');}
  reset(){this.live();if(this.running)throw new Error('分析期間無法重設姿態追蹤器。');this.wasm.pose_reset();}
  async analyze(image,time){
    this.live();if(this.running)throw new Error('同一姿態追蹤器不可同時分析影格。');
    if(!image||!Number.isInteger(image.width)||!Number.isInteger(image.height)||image.width<1||image.height<1||image.width>1920||image.height>1920||image.width*image.height>1920*1080||image.pixels?.length!==image.width*image.height*4||!Number.isFinite(time)||time<0||!Number.isFinite(Math.fround(time)))throw new Error('影像上限為 1920 × 1080（支援直式），時間必須是非負有限毫秒數。');
    const operation=async()=>{
      const start=performance.now();let inference=0;
      const bits=new Float32Array([time]);const detect=this.wasm.pose_begin(image.width,image.height,new Int32Array(bits.buffer)[0]);
      if(detect===-2)throw new Error('姿態影像或時間無效。');
      if(detect>=0){
        const target=new Uint8Array(this.wasm.memory.buffer,this.wasm.pose_ptr(0),image.pixels.length);target.set(image.pixels);
        if(image.alpha==='Premultiplied')for(let i=0;i<target.length;i+=4)for(let c=0;c<3;c++)target[i+c]=target[i+3]?Math.min(255,Math.round(target[i+c]*255/target[i+3])):0;
        if(detect===1){
          this.wasm.pose_detector_input();let t=performance.now();const d=await this.run(this.detector,1,[this.boxes,this.scores]);inference+=performance.now()-t;
          this.live();this.write(2,d.get(this.boxes));this.write(3,d.get(this.scores));
          if(!this.wasm.pose_detect())return {hasPose:false,confidence:0,landmarks:[],totalMilliseconds:performance.now()-start,inferenceMilliseconds:inference,detectionRan:true};
        }
        this.wasm.pose_landmark_input();let t=performance.now();const l=await this.run(this.landmark,4,[this.points,this.confidence,this.heatmap]);inference+=performance.now()-t;
        this.live();this.write(5,l.get(this.points));this.write(7,l.get(this.confidence));this.write(6,l.get(this.heatmap));this.wasm.pose_finish();
      }
      const r=this.floats(8,166),hasPose=r[0]>=.5;
      return {hasPose,confidence:r[0],landmarks:hasPose?Array.from(r.subarray(1)):[],totalMilliseconds:performance.now()-start,inferenceMilliseconds:inference,detectionRan:detect===1};
    };
    this.running=operation();try{return await this.running;}finally{this.running=null;}
  }
  async dispose(){if(this.disposed)return;this.disposed=true;try{await this.running;}catch{}finally{try{await this.landmark.dispose();}finally{await this.detector.dispose();}}}
}

export function registerMediaPipe(host){
  const m=host.library('MediaPipe');host.modules.set('姿態追蹤',m);const trackers=new Set();
  host.pose={trackers,async dispose(){await Promise.all([...trackers].map(t=>t.dispose()));trackers.clear();}};
  const field=(m,en,zh,v)=>{host.set(m,en,v);host.set(m,zh,v);};
  host.bind(m,'MediaPipe','CreatePoseTracker',async(detector,landmark,provider)=>{
    if(trackers.size>=4)throw new Error('最多只能同時建立四個姿態追蹤器。');
    const model=path=>host.modelAsset?.(path)??host.asset(path);
    const tracker=await BrowserPoseTracker.create(host.onnx,model(detector),model(landmark),provider,host.signal);trackers.add(tracker);const t=host.newModule();
    host.bind(t,'MediaPipe.PoseTracker','Analyze',async(image,time)=>{
      const r=await tracker.analyze(host.application.own(image),time),out=host.newModule();
      for(const [en,zh,v]of [['HasPose','有人物',r.hasPose],['Confidence','信心',r.confidence],['Landmarks','關鍵點',r.landmarks.map(v=>num(v,'double'))],['TotalMilliseconds','總耗時',r.totalMilliseconds],['InferenceMilliseconds','模型耗時',r.inferenceMilliseconds],['DetectionRan','執行人物偵測',r.detectionRan]])field(out,en,zh,v);
      return out;
    });
    host.bind(t,'MediaPipe.PoseTracker','Reset',()=>tracker.reset());
    host.bind(t,'MediaPipe.PoseTracker','Dispose',async()=>{trackers.delete(tracker);await tracker.dispose();});return t;
  });
}
