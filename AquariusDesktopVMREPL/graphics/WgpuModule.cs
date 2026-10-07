using System.Numerics;
using AquariusLang.Object;
using AquaEnvironment=AquariusLang.Object.Environment;

namespace AquariusREPL.Graphics;

internal sealed partial class GraphicsRuntime {
    private readonly Dictionary<ModuleObj,WgpuDevice> wgpuDevices=new();
    private readonly Dictionary<ModuleObj,WgpuBuffer> wgpuBuffers=new();
    private readonly Dictionary<ModuleObj,WgpuShader> wgpuShaders=new();
    private void RegisterWgpu() {
        var env=Module("WGPU");modules["wgpu"]=modules["WGPU"];
        env.Create("Backend",new StringObj("wgpu-native"));
        Bind(env,"CreateDevice",0,_=>CreateWgpuDevice());
    }
    private static float[] WgpuFloats(IObject value) {
        var array=Array(value);if(array.Elements.Length==0||array.Elements.Length>16*1024*1024)throw new ArgumentException("wgpu data needs 1..16 million floats.");
        return array.Elements.Select(F).ToArray();
    }
    private static Vector4 WgpuColor(IObject value) {
        var data=WgpuFloats(value);if(data.Length!=4||data.Any(x=>x<0||x>1))throw new ArgumentException("Color needs four RGBA values in 0..1.");return new(data[0],data[1],data[2],data[3]);
    }
    private ModuleObj CreateWgpuDevice() {
        var device=new WgpuDevice();var env=AquaEnvironment.NewEnvironment();var module=new ModuleObj(env);wgpuDevices.Add(module,device);
        env.Create("Backend",new StringObj(device.Backend));
        void Action(AquaEnvironment scope,string name,int count,Func<IObject[],IObject> fn)=>Bind(scope,name,count,a=>{device.RequireLive();return fn(a);});
        Bind(env,"Dispose",0,_=>{device.Dispose();return Null();});
        Action(env,"Poll",0,_=>{device.Flush();device.Poll();return Null();});
        Action(env,"CreateBuffer",1,a=> {
            var buffer=new WgpuBuffer(device,WgpuFloats(a[0]));var bufferEnv=AquaEnvironment.NewEnvironment();var bufferModule=new ModuleObj(bufferEnv);wgpuBuffers.Add(bufferModule,buffer);
            bufferEnv.Create("Length",N(buffer.Length));
            Action(bufferEnv,"Write",1,x=>{buffer.Write(WgpuFloats(x[0]));return Null();});
            Action(bufferEnv,"Read",0,_=>Numbers(buffer.Read().Select(x=>(double)x).ToArray()));
            Bind(bufferEnv,"Dispose",0,_=>{buffer.Dispose();return Null();});return bufferModule;
        });
        Action(env,"Dispatch",3,a=> {
            if(a[1] is not ModuleObj bufferModule||!wgpuBuffers.TryGetValue(bufferModule,out var buffer)||buffer.Device!=device)throw new ArgumentException("Expected a live buffer from this wgpu device.");
            int groups=Int(a[2]);if(groups<1||groups>65535)throw new ArgumentException("Workgroup count must be 1..65535.");
            device.Dispatch(Text(a[0]),buffer,(uint)groups);return Null();
        });
        Action(env,"CreateShader",2,a=> {
            var shader=new WgpuShader(device,Text(a[0]),Text(a[1]));var shaderEnv=AquaEnvironment.NewEnvironment();var shaderModule=new ModuleObj(shaderEnv);wgpuShaders.Add(shaderModule,shader);
            Action(shaderEnv,"Set",2,x=>{if(shader.Disposed)throw new InvalidOperationException("wgpu shader has been disposed.");shader.Uniforms.Set(Text(x[0]),WgpuFloats(x[1]));return Null();});
            Action(shaderEnv,"SetInt",2,x=>{if(shader.Disposed)throw new InvalidOperationException("wgpu shader has been disposed.");shader.Uniforms.Set(Text(x[0]),new float[1],true,Int(x[1]));return Null();});
            Bind(shaderEnv,"Dispose",0,_=>{shader.Dispose();return Null();});return shaderModule;
        });
        Action(env,"CreateRenderTarget",2,a=> {
            var target=new WgpuTarget(device,Dimension(a[0]),Dimension(a[1]));var targetEnv=AquaEnvironment.NewEnvironment();var targetModule=new ModuleObj(targetEnv);
            var canvas=new ProcessingCanvas(this,target.Width,target.Height,false){Model=Matrix4x4.Identity,View=Matrix4x4.Identity,Projection=Matrix4x4.Identity};
            targetEnv.Create("Width",N(target.Width));targetEnv.Create("Height",N(target.Height));
            void Live(){if(target.Disposed)throw new InvalidOperationException("wgpu render target has been disposed.");}
            Action(targetEnv,"Clear",1,x=>{Live();target.Clear(WgpuColor(x[0]));return Null();});
            Action(targetEnv,"Draw",2,x=> {
                Live();if(x[0] is not ModuleObj shaderModule||!wgpuShaders.TryGetValue(shaderModule,out var shader)||shader.Device!=device||shader.Disposed)throw new ArgumentException("Expected a live shader from this wgpu device.");
                var vertices=WgpuFloats(x[1]);if(vertices.Length%36!=0)throw new ArgumentException("Triangle vertices need a multiple of 36 floats (12 per vertex).");
                target.Draw(shader,vertices,WgpuShaders.Uniforms(canvas,false,false,false),4,0,false,null);return Null();
            });
            Action(targetEnv,"ReadPixels",0,_=>{Live();return PixelArray(target.ReadPixels());});
            Action(targetEnv,"Save",1,x=>{Live();WriteImage(Text(x[0]),target.Width,target.Height,target.ReadPixels());return Null();});
            Bind(targetEnv,"Dispose",0,_=>{target.Dispose();return Null();});return targetModule;
        });
        return module;
    }
}
