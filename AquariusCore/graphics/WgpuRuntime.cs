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
    private readonly Dictionary<ModuleObj,IWgpuRenderTarget> wgpuTargets=new();
    private readonly Dictionary<ModuleObj,IWgpuVertexBuffer> wgpuVertexBuffers=new();
    private readonly Dictionary<ModuleObj,IWgpuWindow> wgpuWindows=new();
    private void RegisterWgpu() {
        var env=Module("WGPU");modules["wgpu"]=modules["WGPU"];
        env.Create("Backend",new StringObj(backend.Name));
        Bind(env,"CreateDevice",0,1,a=>CreateWgpuDevice(a.Length==1?WindowArgument(a[0]):null));
        Bind(env,"CreateWindow",3,a=>CreateWgpuWindow(Dimension(a[0]),Dimension(a[1]),Text(a[2])));
    }
    private IWgpuWindow WindowArgument(IObject value) {
        if(value is not ModuleObj m||!wgpuWindows.TryGetValue(m,out var window)||window.Disposed)throw new ArgumentException("Expected a live WGPU window.");
        return window;
    }
    private static bool Bool(IObject o) => o is BooleanObj b ? b.Value : throw new ArgumentException("Expected a Boolean.");
    private static WgpuDrawState DrawState(IObject value) {
        if(value is not HashObj hash)throw new ArgumentException("SetState expects a hash such as {\"depth\":\"less\",\"blend\":\"replace\",\"cull\":\"back\"}.");
        var state=WgpuDrawState.Default;
        foreach(var pair in hash.Pairs.Values) {
            string key=pair.Key is StringObj k?k.Value:throw new ArgumentException("Draw state keys must be strings.");
            string text=pair.Value is StringObj v?v.Value:throw new ArgumentException("Draw state values must be strings.");
            state=key switch {"depth"=>state with {Depth=text},"blend"=>state with {Blend=text},"cull"=>state with {Cull=text},
                _=>throw new ArgumentException($"Unknown draw state key '{key}'; expected depth, blend or cull.")};
        }
        return state.Validate();
    }
    private ModuleObj CreateWgpuWindow(int width,int height,string title) {
        var window=backend.CreateWindow(width,height,title);var env=AquaEnvironment.NewEnvironment();var module=new ModuleObj(env);wgpuWindows.Add(module,window);
        void Publish() {
            var s=window.State;
            env.Create("Width",N(s.Width));env.Create("Height",N(s.Height));env.Create("PixelWidth",N(s.PixelWidth));env.Create("PixelHeight",N(s.PixelHeight));
            env.Create("MouseX",new DoubleObj(s.MouseX));env.Create("MouseY",new DoubleObj(s.MouseY));
            env.Create("MouseDeltaX",new DoubleObj(s.MouseDeltaX));env.Create("MouseDeltaY",new DoubleObj(s.MouseDeltaY));env.Create("Wheel",new DoubleObj(s.Wheel));
            env.Create("Focused",new BooleanObj(s.Focused));env.Create("PointerLocked",new BooleanObj(s.PointerLocked));
            env.Create("ShouldClose",new BooleanObj(s.ShouldClose));env.Create("Resized",new BooleanObj(s.Resized));
        }
        void Live(){if(window.Disposed)throw new InvalidOperationException("WGPU window has been disposed.");}
        void W(string name,int count,Func<IObject[],IObject> fn)=>Bind(env,name,count,a=>{Live();return fn(a);});
        Publish();
        W("Poll",0,_=>{window.Poll();Publish();return Null();});
        W("KeyDown",1,a=>new BooleanObj(window.KeyDown(Int(a[0]))));
        W("KeyPressed",1,a=>new BooleanObj(window.KeyPressed(Int(a[0]))));
        W("KeyReleased",1,a=>new BooleanObj(window.KeyReleased(Int(a[0]))));
        W("MouseDown",1,a=>new BooleanObj(window.MouseDown(Int(a[0]))));
        W("MousePressed",1,a=>new BooleanObj(window.MousePressed(Int(a[0]))));
        W("MouseReleased",1,a=>new BooleanObj(window.MouseReleased(Int(a[0]))));
        W("SetPointerLock",1,a=>{window.SetPointerLock(Bool(a[0]));Publish();return Null();});
        W("SetTitle",1,a=>{window.SetTitle(Text(a[0]));return Null();});
        W("Close",0,_=>{window.Close();Publish();return Null();});
        Bind(env,"Dispose",0,_=>{window.Dispose();return Null();});
        return module;
    }
    private static float[] WgpuFloats(IObject value) {
        var array=Array(value);if(array.Elements.Length==0||array.Elements.Length>16*1024*1024)throw new ArgumentException("wgpu data needs 1..16 million floats.");
        return array.Elements.Select(F).ToArray();
    }
    private static Vector4 WgpuColor(IObject value) {
        var data=WgpuFloats(value);if(data.Length!=4||data.Any(x=>x<0||x>1))throw new ArgumentException("Color needs four RGBA values in 0..1.");return new(data[0],data[1],data[2],data[3]);
    }
    private ModuleObj CreateWgpuDevice(IWgpuWindow? window=null) {
        var device=window==null?backend.CreateDevice():backend.CreateDevice(window);var env=AquaEnvironment.NewEnvironment();var module=new ModuleObj(env);wgpuDevices.Add(module,device);
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
        Action(env,"Present",1,a=>{device.Present(Target(a[0],device));return Null();});
        Action(env,"CreateVertexBuffer",1,a=> {
            var values=WgpuFloats(a[0]);if(values.Length%12!=0)throw new ArgumentException("Vertex buffers need a multiple of 12 floats per vertex (position, normal, UV, RGBA).");
            var buffer=device.CreateVertexBuffer(values);var vertexEnv=AquaEnvironment.NewEnvironment();var vertexModule=new ModuleObj(vertexEnv);wgpuVertexBuffers.Add(vertexModule,buffer);
            vertexEnv.Create("Length",N(buffer.Length));
            void Live(){if(buffer.Disposed)throw new InvalidOperationException("wgpu vertex buffer has been disposed.");}
            Action(vertexEnv,"Write",1,x=>{Live();buffer.Write(WgpuFloats(x[0]));return Null();});
            Action(vertexEnv,"WriteRange",2,x=>{Live();buffer.WriteRange(Int(x[0]),WgpuFloats(x[1]));return Null();});
            Bind(vertexEnv,"Dispose",0,_=>{buffer.Dispose();return Null();});return vertexModule;
        });
        Action(env,"CreateBuffer",1,a=> {
            var buffer=a[0] is ArrayObj?device.CreateBuffer(WgpuFloats(a[0])):device.CreateBuffer(WgpuLimits.BufferLength(Int(a[0])));var bufferEnv=AquaEnvironment.NewEnvironment();var bufferModule=new ModuleObj(bufferEnv);wgpuBuffers.Add(bufferModule,buffer);
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
            Action(shaderEnv,"SetBuffer",2,x=>{
                if(x[1] is not ModuleObj m||!wgpuBuffers.TryGetValue(m,out var buffer)||buffer.Owner!=device)throw new ArgumentException("Expected a live storage buffer from this device.");
                shader.SetBuffer(Text(x[0]),buffer);return Null();
            });
            Bind(shaderEnv,"Dispose",0,_=>{shader.Dispose();return Null();});return shaderModule;
        });
        Action(env,"CreateRenderTarget",2,a=> {
            string format=a.Length==3?Text(a[2]):"rgba8unorm";if(format is not ("rgba8unorm" or "rgba16float"))throw new ArgumentException("Render target format must be rgba8unorm or rgba16float.");
            var target=device.CreateRenderTarget(Dimension(a[0]),Dimension(a[1]),format);var targetEnv=AquaEnvironment.NewEnvironment();var targetModule=new ModuleObj(targetEnv);wgpuTargets.Add(targetModule,target);
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
            Action(targetEnv,"SetState",1,x=>{Live();target.SetState(DrawState(x[0]));return Null();});
            Action(targetEnv,"DrawBuffer",2,x=> {
                Live();var shader=Shader(x[0],device);
                if(x[1] is not ModuleObj bm||!wgpuVertexBuffers.TryGetValue(bm,out var buffer)||buffer.Owner!=device||buffer.Disposed)throw new ArgumentException("Expected a live vertex buffer from this wgpu device.");
                int vertices=buffer.Length/12,first=x.Length>2?Int(x[2]):0,count=x.Length>3?Int(x[3]):vertices-first;
                WgpuLimits.VertexRange(vertices,first,count);if(count>0)target.DrawBuffer(shader,buffer,first,count);return Null();
            },4);
            Action(targetEnv,"DrawProcedural",2,x=> {
                Live();var shader=Shader(x[0],device);int count=Int(x[1]),first=x.Length>2?Int(x[2]):0;
                WgpuLimits.ProceduralRange(first,count);if(count>0)target.DrawProcedural(shader,first,count);return Null();
            },3);
            Action(targetEnv,"ReadPixels",0,_=>{Live();return PixelArray(target.ReadPixels());});
            Action(targetEnv,"Save",1,x=>{Live();saveImage(Text(x[0]),target.Width,target.Height,target.ReadPixels());return Null();});
            Bind(targetEnv,"Dispose",0,_=>{target.Dispose();return Null();});return targetModule;
        },3);
        return module;
    }
    private IWgpuShader Shader(IObject value,IWgpuDevice device) {
        if(value is not ModuleObj m||!wgpuShaders.TryGetValue(m,out var shader)||shader.Owner!=device||shader.Disposed)throw new ArgumentException("Expected a live shader from this wgpu device.");
        return shader;
    }
    private IWgpuRenderTarget Target(IObject value,IWgpuDevice device) {
        if(value is not ModuleObj m||!wgpuTargets.TryGetValue(m,out var target))throw new ArgumentException("Expected a render target from this wgpu device.");
        return target;
    }
}


