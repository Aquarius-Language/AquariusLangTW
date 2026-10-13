using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AquariusLang.Application;
using AquariusLang.Object;
using AquariusLang.Onnx;
using AquariusLang.runtime;
using AquariusLang.Wasm;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLang.MediaPipe;

public static class PoseCatalog {
    public static IReadOnlyList<LibraryFunction> Functions {get;} = Array.AsReadOnly(new[] {
        new LibraryFunction("MediaPipe","CreatePoseTracker","建立姿態追蹤器",3,3,"MediaPipe.PoseTracker"),
        new("MediaPipe.PoseTracker","Analyze","分析",2,2),
        new("MediaPipe.PoseTracker","Reset","重設",0,0),
        new("MediaPipe.PoseTracker","Dispose","釋放",0,0)
    });
}
public sealed class PoseRuntime : IDisposable {
    private readonly ModuleObj module = new(AquaEnvironment.NewEnvironment());
    private readonly HashSet<PoseTracker> trackers = new();
    private bool disposed;
    public PoseRuntime(IWasmEngine engine,IOnnxBackend backend,Func<string,byte[]> readModel,Func<IObject,PixelImage> image) {
        Bind(module,"MediaPipe","CreatePoseTracker",args=>{
            if(trackers.Count>=4)throw new ArgumentException("最多只能同時建立四個姿態追蹤器。");
            var provider=Text(args[2]) switch {"CPU"=>OnnxProvider.CPU,"WebGPU"=>OnnxProvider.WebGPU,_=>throw new ArgumentException("後端必須是 WebGPU 或 CPU。")};
            var tracker=new PoseTracker(engine,backend,readModel(Text(args[0])),readModel(Text(args[1])),provider);trackers.Add(tracker);
            var m=new ModuleObj(AquaEnvironment.NewEnvironment());
            Bind(m,"MediaPipe.PoseTracker","Analyze",a=>{
                var r=tracker.Analyze(image(a[0]),a[1] is INumberObj n?n.GetNumValue():throw new ArgumentException("時間必須是毫秒數。"));
                var result=new ModuleObj(AquaEnvironment.NewEnvironment());
                Field(result,"HasPose","有人物",new BooleanObj(r.HasPose));Field(result,"Confidence","信心",new DoubleObj(r.Confidence));
                Field(result,"Landmarks","關鍵點",new ArrayObj(r.Landmarks.Select(v=>(IObject)new DoubleObj(v)).ToArray()));
                Field(result,"TotalMilliseconds","總耗時",new DoubleObj(r.TotalMilliseconds));
                Field(result,"InferenceMilliseconds","模型耗時",new DoubleObj(r.InferenceMilliseconds));
                Field(result,"DetectionRan","執行人物偵測",new BooleanObj(r.DetectionRan));return result;
            });
            Bind(m,"MediaPipe.PoseTracker","Reset",_=>{tracker.Reset();return RepeatedPrimitives.NULL;});
            Bind(m,"MediaPipe.PoseTracker","Dispose",_=>{if(trackers.Remove(tracker))tracker.Dispose();return RepeatedPrimitives.NULL;});return m;
        });
    }
    public bool TryImport(string name,out ModuleObj result){result=module;return name is "MediaPipe" or "姿態追蹤";}
    private void Bind(ModuleObj m,string library,string name,Func<IObject[],IObject> action){
        var spec=PoseCatalog.Functions.Single(f=>f.Library==library&&f.EnglishName==name);
        var fn=new BuiltinObj(args=>{try {ObjectDisposedException.ThrowIf(disposed,this);if(args.Length!=spec.MinimumArguments)throw new ArgumentException("參數數量無效。");return action(args);}
            catch(Exception e) when(e is ArgumentException or InvalidOperationException or OnnxFailure or IOException or ApplicationFailure){return new ErrorObj($"姿態追蹤.{spec.TraditionalChineseName}：{e.Message}");}})
            {RetainedEnvironment=m._Environment,PreservesArguments=true};
        FunctionRegistration.Define(m._Environment,fn,spec.TraditionalChineseName,name);
    }
    private static string Text(IObject o)=>o is StringObj s?s.Value:throw new ArgumentException("預期字串參數。");
    private static void Field(ModuleObj m,string english,string chinese,IObject value){m._Environment.Create(english,value);m._Environment.Create(chinese,value);}
    public void Dispose(){if(disposed)return;disposed=true;foreach(var t in trackers)t.Dispose();trackers.Clear();}
}
