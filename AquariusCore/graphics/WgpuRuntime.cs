using System.Numerics;
using System;
using System.Collections.Generic;
using System.Linq;
using AquariusLang.Object;
using AquaEnvironment=AquariusLang.Object.Environment;

namespace AquariusLang.Graphics;

public sealed partial class WgpuRuntime : IDisposable {
    private readonly Dictionary<ModuleObj,IWgpuDevice> wgpuDevices=new();
    private readonly Dictionary<ModuleObj,IWgpuBuffer> wgpuBuffers=new();
    private readonly Dictionary<ModuleObj,IWgpuShader> wgpuShaders=new();
    private readonly Dictionary<ModuleObj,IWgpuTexture> wgpuTextures=new();
    private void RegisterWgpu() {
        var env=Module("WGPU");modules["wgpu"]=modules["WGPU"];
        env.Create("Backend",new StringObj(backend.Name));
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
        var device=backend.CreateDevice();var env=AquaEnvironment.NewEnvironment();var module=new ModuleObj(env);wgpuDevices.Add(module,device);
        env.Create("Backend",new StringObj(device.Backend));
        void Action(AquaEnvironment scope,string name,int count,Func<IObject[],IObject> fn,int? max=null)=>Bind(scope,name,count,max??count,a=>{device.RequireLive();return fn(a);});
        Bind(env,"Dispose",0,_=>{device.Dispose();return Null();});
        Action(env,"Poll",0,_=>{device.Poll();return Null();});
        Action(env,"LoadTexture",6,a=> {
            var descriptor=new WgpuTextureDescriptor(Int(a[1]),Int(a[2]),Text(a[3]),Text(a[4]),Int(a[5]));
            _=descriptor.ByteLength;
            var bytes=descriptor.Decode(readResource(Text(a[0])));
            var texture=device.CreateTexture(descriptor,bytes);var scope=AquaEnvironment.NewEnvironment();var module=new ModuleObj(scope);wgpuTextures.Add(module,texture);
            Bind(scope,"Dispose",0,_=>{texture.Dispose();return Null();});return module;
        });
        Action(env,"CreateBuffer",1,a=> {
            var buffer=device.CreateBuffer(WgpuFloats(a[0]));var bufferEnv=AquaEnvironment.NewEnvironment();var bufferModule=new ModuleObj(bufferEnv);wgpuBuffers.Add(bufferModule,buffer);
            bufferEnv.Create("Length",N(buffer.Length));
            Action(bufferEnv,"Write",1,x=>{buffer.Write(WgpuFloats(x[0]));return Null();});
            Action(bufferEnv,"Read",0,_=>Numbers(buffer.Read().Select(x=>(double)x).ToArray()));
            Action(bufferEnv,"WriteRange",2,x=>{buffer.WriteRange(Int(x[0]),WgpuFloats(x[1]));return Null();});
            Action(bufferEnv,"ReadRange",2,x=>Numbers(buffer.ReadRange(Int(x[0]),Int(x[1])).Select(v=>(double)v).ToArray()));
            Bind(bufferEnv,"Dispose",0,_=>{buffer.Dispose();return Null();});return bufferModule;
        });
        Action(env,"Dispatch",3,a=> {
            if(a[1] is not ModuleObj bufferModule||!wgpuBuffers.TryGetValue(bufferModule,out var buffer)||buffer.Owner!=device)throw new ArgumentException("Expected a live buffer from this wgpu device.");
            int groups=Int(a[2]);if(groups<1||groups>65535)throw new ArgumentException("Workgroup count must be 1..65535.");
            device.Dispatch(Text(a[0]),buffer,(uint)groups);return Null();
        });
        Action(env,"CreateShader",2,a=> {
            var shader=device.CreateShader(Text(a[0]),Text(a[1]));var shaderEnv=AquaEnvironment.NewEnvironment();var shaderModule=new ModuleObj(shaderEnv);wgpuShaders.Add(shaderModule,shader);
            Action(shaderEnv,"Set",2,x=>{if(shader.Disposed)throw new InvalidOperationException("wgpu shader has been disposed.");shader.Set(Text(x[0]),WgpuFloats(x[1]));return Null();});
            Action(shaderEnv,"SetInt",2,x=>{if(shader.Disposed)throw new InvalidOperationException("wgpu shader has been disposed.");shader.SetInt(Text(x[0]),Int(x[1]));return Null();});
            Action(shaderEnv,"SetTexture",2,x=>{
                if(x[1] is not ModuleObj m||!wgpuTextures.TryGetValue(m,out var texture)||texture.Owner!=device||texture.Disposed)throw new ArgumentException("Expected a live texture from this device.");
                shader.SetTexture(Text(x[0]),texture);return Null();
            });
            Bind(shaderEnv,"Dispose",0,_=>{shader.Dispose();return Null();});return shaderModule;
        });
        Action(env,"CreateRenderTarget",2,a=> {
            string format=a.Length==3?Text(a[2]):"rgba8unorm";if(format is not ("rgba8unorm" or "rgba16float"))throw new ArgumentException("Render target format must be rgba8unorm or rgba16float.");
            var target=device.CreateRenderTarget(Dimension(a[0]),Dimension(a[1]),format);var targetEnv=AquaEnvironment.NewEnvironment();var targetModule=new ModuleObj(targetEnv);
            targetEnv.Create("Width",N(target.Width));targetEnv.Create("Height",N(target.Height));
            Action(targetEnv,"GetTexture",0,_=> {
                var texture=target.GetTexture();var scope=AquaEnvironment.NewEnvironment();var module=new ModuleObj(scope);wgpuTextures.Add(module,texture);
                Bind(scope,"Dispose",0,_=>{texture.Dispose();return Null();});return module;
            });
            void Live(){device.RequireLive();}
            Action(targetEnv,"Clear",1,x=>{Live();target.Clear(WgpuColor(x[0]));return Null();});
            Action(targetEnv,"Draw",2,x=> {
                Live();if(x[0] is not ModuleObj shaderModule||!wgpuShaders.TryGetValue(shaderModule,out var shader)||shader.Owner!=device||shader.Disposed)throw new ArgumentException("Expected a live shader from this wgpu device.");
                var vertices=WgpuFloats(x[1]);if(vertices.Length%36!=0)throw new ArgumentException("Triangle vertices need a multiple of 36 floats (12 per vertex).");
                target.Draw(shader,vertices);return Null();
            });
            Action(targetEnv,"ReadPixels",0,_=>{Live();return PixelArray(target.ReadPixels());});
            Action(targetEnv,"Save",1,x=>{Live();saveImage(Text(x[0]),target.Width,target.Height,target.ReadPixels());return Null();});
            Bind(targetEnv,"Dispose",0,_=>{target.Dispose();return Null();});return targetModule;
        },3);
        return module;
    }
}


