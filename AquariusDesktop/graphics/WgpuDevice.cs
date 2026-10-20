using System.Numerics;
using AquariusLang.Graphics;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.WebGPU;
using Silk.NET.WebGPU.Extensions.WGPU;
using GpuBuffer=Silk.NET.WebGPU.Buffer;
using GpuColor=Silk.NET.WebGPU.Color;

namespace AquariusLang.Desktop.Graphics;

// The native API is private to this owner. Aquarius never receives numeric pointers.
internal unsafe sealed partial class WgpuDevice : IDisposable {
    // Native worker threads can outlive a device. Keep the shared DLL loaded for
    // the process lifetime, as with a normal DllImport, while releasing all GPU handles.
    private static readonly Lazy<WebGPU> ProcessApi=new(WebGPU.GetApi);
    private static readonly object ExtensionGate=new();
    private static Wgpu? processExtension;
    internal readonly WebGPU Api=ProcessApi.Value;
    internal Wgpu Extension=null!;
    internal Device* Handle;
    internal Queue* Queue;
    private Instance* instance;
    private Adapter* adapter;
    private Surface* surface;
    private SurfaceConfiguration configuration;
    internal BindGroupLayout* DrawingLayout;
    internal BindGroupLayout* UserLayout;
    internal PipelineLayout* DrawingPipelineLayout;
    private BindGroupLayout* blitLayout;
    private PipelineLayout* blitPipelineLayout;
    private ShaderModule* blitShader;
    internal Sampler* Sampler;
    internal WgpuTexture White=null!;
    internal readonly HashSet<IDisposable> Resources=new();
    internal bool Disposed;
    private string? error;
    private readonly ErrorCallback errorCallback;
    private readonly Dictionary<(TextureFormat,uint),nint> blitPipelines=new();
    internal string Backend {get;private set;}="";

    internal WgpuDevice(IntPtr window=default) {
        errorCallback=(type,message,_)=>error=$"{type}: {Marshal.PtrToStringUTF8((nint)message)}";
        try {
            // Linux's accelerated GLES adapter can outrank a Vulkan CPU adapter
            // while lacking compute/storage features required by WebGPU.
            var extras = new InstanceExtras {
                Chain = new ChainedStruct { SType = (SType)NativeSType.STypeInstanceExtras },
                Backends = InstanceBackend.Vulkan
            };
            var instanceDescriptor = new InstanceDescriptor { NextInChain = &extras.Chain };
            instance=Api.CreateInstance(OperatingSystem.IsLinux() ? &instanceDescriptor : null);
            if(instance==null)throw new InvalidOperationException("wgpu instance creation failed.");
            if(window!=IntPtr.Zero)CreateSurface(window);
            RequestAdapterCallback adapterCallback=(status,value,message,_)=> {
                if(status==RequestAdapterStatus.Success)adapter=value;
                else error="wgpu adapter unavailable: "+Marshal.PtrToStringUTF8((nint)message);
            };
            var options=new RequestAdapterOptions { CompatibleSurface=surface, PowerPreference=PowerPreference.HighPerformance };
            Api.InstanceRequestAdapter(instance,&options,adapterCallback,null);
            GC.KeepAlive(adapterCallback);
            if(adapter==null)throw new InvalidOperationException(error??"wgpu adapter unavailable.");
            AdapterProperties properties=default;Api.AdapterGetProperties(adapter,&properties);Backend=properties.BackendType.ToString();
            RequestDeviceCallback deviceCallback=(status,value,message,_)=> {
                if(status==RequestDeviceStatus.Success)Handle=value;
                else error="wgpu device creation failed: "+Marshal.PtrToStringUTF8((nint)message);
            };
            var descriptor=new DeviceDescriptor();
            Api.AdapterRequestDevice(adapter,&descriptor,deviceCallback,null);GC.KeepAlive(deviceCallback);
            if(Handle==null)throw new InvalidOperationException(error??"wgpu device creation failed.");
            Api.DeviceSetUncapturedErrorCallback(Handle,errorCallback,null);
            lock(ExtensionGate) {
                if(processExtension==null) {
                    if(!Api.TryGetDeviceExtension(Handle,out Wgpu ext))throw new InvalidOperationException("wgpu-native polling extension unavailable.");
                    processExtension=ext;
                }
                Extension=processExtension;
            }
            Queue=Api.DeviceGetQueue(Handle);
            CreateLayouts();
            White=new WgpuTexture(this,1,1);White.Upload(new byte[]{255,255,255,255});
            if(surface!=null) {
                SurfaceCapabilities caps=default;Api.SurfaceGetCapabilities(surface,adapter,&caps);
                if(caps.FormatCount==0)throw new InvalidOperationException("wgpu surface has no supported formats.");
                configuration=new SurfaceConfiguration { Device=Handle,Format=caps.Formats[0],Usage=TextureUsage.RenderAttachment,
                    AlphaMode=caps.AlphaModes[0],PresentMode=PresentMode.Fifo };
                // Prefer a linear format: Processing colors are already expressed as display values.
                for(nuint i=0;i<caps.FormatCount;i++)if(caps.Formats[i] is TextureFormat.Bgra8Unorm or TextureFormat.Rgba8Unorm){configuration.Format=caps.Formats[i];break;}
                Api.SurfaceCapabilitiesFreeMembers(caps);
            }
            Check();
        } catch {Dispose();throw;}
    }
    private void CreateSurface(IntPtr window) {
        int kind=Native.aqua_wgpu_handles(window,out var display,out var handle);
        SurfaceDescriptor descriptor=default;
        var win=new SurfaceDescriptorFromWindowsHWND { Chain=new ChainedStruct { SType=SType.SurfaceDescriptorFromWindowsHwnd },Hinstance=(void*)display,Hwnd=(void*)handle };
        var x11=new SurfaceDescriptorFromXlibWindow { Chain=new ChainedStruct { SType=SType.SurfaceDescriptorFromXlibWindow },Display=(void*)display,Window=(ulong)handle };
        var wayland=new SurfaceDescriptorFromWaylandSurface { Chain=new ChainedStruct { SType=SType.SurfaceDescriptorFromWaylandSurface },Display=(void*)display,Surface=(void*)handle };
        var metal=new SurfaceDescriptorFromMetalLayer { Chain=new ChainedStruct { SType=SType.SurfaceDescriptorFromMetalLayer },Layer=(void*)handle };
        descriptor.NextInChain=kind switch{1=>&win.Chain,2=>&x11.Chain,3=>&wayland.Chain,4=>&metal.Chain,_=>throw new InvalidOperationException("Unsupported GLFW surface platform.")};
        surface=Api.InstanceCreateSurface(instance,&descriptor);
        if(surface==null)throw new InvalidOperationException("wgpu surface creation failed.");
    }
    private void CreateLayouts() {
        var entries=stackalloc BindGroupLayoutEntry[3];
        entries[0]=new(){Binding=0,Visibility=ShaderStage.Vertex|ShaderStage.Fragment,Buffer=new(){Type=BufferBindingType.Uniform,MinBindingSize=WgpuShaders.UniformBytes}};
        entries[1]=new(){Binding=1,Visibility=ShaderStage.Fragment,Texture=new(){SampleType=TextureSampleType.Float,ViewDimension=TextureViewDimension.Dimension2D}};
        entries[2]=new(){Binding=2,Visibility=ShaderStage.Fragment,Sampler=new(){Type=SamplerBindingType.Filtering}};
        var descriptor=new BindGroupLayoutDescriptor {EntryCount=3,Entries=entries};DrawingLayout=Api.DeviceCreateBindGroupLayout(Handle,&descriptor);
        var user=new BindGroupLayoutEntry { Binding=0,Visibility=ShaderStage.Vertex|ShaderStage.Fragment,Buffer=new(){Type=BufferBindingType.Uniform,MinBindingSize=1024} };
        descriptor=new(){EntryCount=1,Entries=&user};UserLayout=Api.DeviceCreateBindGroupLayout(Handle,&descriptor);
        var layouts=stackalloc BindGroupLayout*[2]{DrawingLayout,UserLayout};
        var pipeline=new PipelineLayoutDescriptor {BindGroupLayoutCount=2,BindGroupLayouts=layouts};DrawingPipelineLayout=Api.DeviceCreatePipelineLayout(Handle,&pipeline);
        var blit=stackalloc BindGroupLayoutEntry[2];blit[0]=entries[1];blit[0].Binding=0;blit[1]=entries[2];blit[1].Binding=1;
        descriptor=new(){EntryCount=2,Entries=blit};blitLayout=Api.DeviceCreateBindGroupLayout(Handle,&descriptor);
        var blitLayoutPointer=blitLayout;
        pipeline=new(){BindGroupLayoutCount=1,BindGroupLayouts=&blitLayoutPointer};blitPipelineLayout=Api.DeviceCreatePipelineLayout(Handle,&pipeline);
        var sampler=new SamplerDescriptor { AddressModeU=AddressMode.ClampToEdge,AddressModeV=AddressMode.ClampToEdge,AddressModeW=AddressMode.ClampToEdge,
            MagFilter=FilterMode.Linear,MinFilter=FilterMode.Linear,MipmapFilter=MipmapFilterMode.Nearest,MaxAnisotropy=1,LodMaxClamp=32 };
        Sampler=Api.DeviceCreateSampler(Handle,&sampler);blitShader=Shader(WgpuShaders.Blit);
    }
    internal void RequireLive() {if(Disposed)throw new InvalidOperationException("wgpu device has been disposed.");}
    internal void Check() {RequireLive();if(error is string message){error=null;throw new InvalidOperationException(message);}}
    internal ShaderModule* Shader(string source) {
        RequireLive();
        var bytes=Encoding.UTF8.GetBytes(source+"\0");
        ShaderModule* shader;
        Api.DevicePushErrorScope(Handle,ErrorFilter.Validation);
        fixed(byte* code=bytes) {
            var wgsl=new ShaderModuleWGSLDescriptor {Chain=new(){SType=SType.ShaderModuleWgslDescriptor},Code=code};
            var descriptor=new ShaderModuleDescriptor {NextInChain=&wgsl.Chain};shader=Api.DeviceCreateShaderModule(Handle,&descriptor);
        }
        string? failure=null;bool complete=false;
        ErrorCallback callback=(type,message,_)=>{if(type!=ErrorType.NoError)failure=Marshal.PtrToStringUTF8((nint)message);complete=true;};
        Api.DevicePopErrorScope(Handle,callback,null);Extension.DevicePoll(Handle,true,null);GC.KeepAlive(callback);
        if(!complete||failure!=null||shader==null){if(shader!=null)Api.ShaderModuleRelease(shader);throw new InvalidOperationException("Shader compile failed: "+(failure??"wgpu did not complete validation."));}
        Check();return shader;
    }
    internal GpuBuffer* Buffer(ulong size,BufferUsage usage,ReadOnlySpan<byte> bytes=default) {
        RequireLive();if(size==0||size>64*1024*1024)throw new ArgumentException("wgpu buffer size must be 1..64 MiB.");
        var descriptor=new BufferDescriptor {Size=(size+3)&~3UL,Usage=usage|BufferUsage.CopyDst};
        var buffer=Api.DeviceCreateBuffer(Handle,&descriptor);Check();
        if(!bytes.IsEmpty)fixed(byte* p=bytes)Api.QueueWriteBuffer(Queue,buffer,0,p,(nuint)bytes.Length);
        return buffer;
    }
    internal GpuBuffer* Floats(float[] values,BufferUsage usage) => Buffer((ulong)values.Length*4,usage,MemoryMarshal.AsBytes(values.AsSpan()));
    internal CommandEncoder* Encoder() => Api.DeviceCreateCommandEncoder(Handle,(CommandEncoderDescriptor*)null);
    internal void Submit(CommandEncoder* encoder) {
        var commands=Api.CommandEncoderFinish(encoder,(CommandBufferDescriptor*)null);Api.QueueSubmit(Queue,1,&commands);
        Api.CommandBufferRelease(commands);Api.CommandEncoderRelease(encoder);Check();
    }
    internal void Flush() {RequireLive();foreach(var target in Resources.OfType<WgpuTarget>().ToArray())target.Flush();}
    internal byte[] ReadBuffer(GpuBuffer* source,int size,ulong offset=0) {
        Flush();
        // MAP_READ is only valid with COPY_DST; it cannot share storage/vertex usage.
        var descriptor=new BufferDescriptor {Size=(ulong)size,Usage=BufferUsage.CopyDst|BufferUsage.MapRead};
        var staging=Api.DeviceCreateBuffer(Handle,&descriptor);
        try {
            var encoder=Encoder();Api.CommandEncoderCopyBufferToBuffer(encoder,source,offset,staging,0,(ulong)size);Submit(encoder);
            return Map(staging,size);
        } finally {Api.BufferRelease(staging);}
    }
    internal byte[] Map(GpuBuffer* buffer,int size) {
        bool done=false;BufferMapAsyncStatus status=default;
        BufferMapCallback callback=(value,_)=>{status=value;done=true;};
        Api.BufferMapAsync(buffer,MapMode.Read,0,(nuint)size,callback,null);Extension.DevicePoll(Handle,true,null);GC.KeepAlive(callback);Check();
        if(!done||status!=BufferMapAsyncStatus.Success)throw new InvalidOperationException("wgpu buffer readback failed: "+status);
        try {var bytes=new byte[size];Marshal.Copy((nint)Api.BufferGetConstMappedRange(buffer,0,(nuint)size),bytes,0,size);return bytes;}
        finally {Api.BufferUnmap(buffer);}
    }
    internal void Blit(TextureView* source,TextureView* destination,TextureView* resolve,TextureFormat format,uint samples) {
        if(!blitPipelines.TryGetValue((format,samples),out var stored)) {
            byte* vs=stackalloc byte[]{(byte)'v',(byte)'s',(byte)'_',(byte)'m',(byte)'a',(byte)'i',(byte)'n',0};
            byte* fs=stackalloc byte[]{(byte)'f',(byte)'s',(byte)'_',(byte)'m',(byte)'a',(byte)'i',(byte)'n',0};
            var color=new ColorTargetState {Format=format,WriteMask=ColorWriteMask.All};
            var fragment=new FragmentState {Module=blitShader,EntryPoint=fs,TargetCount=1,Targets=&color};
            var descriptor=new RenderPipelineDescriptor { Layout=blitPipelineLayout,Vertex=new(){Module=blitShader,EntryPoint=vs},Fragment=&fragment,
                Primitive=new(){Topology=PrimitiveTopology.TriangleList},Multisample=new(){Count=samples,Mask=uint.MaxValue} };
            stored=(nint)Api.DeviceCreateRenderPipeline(Handle,&descriptor);Check();blitPipelines[(format,samples)]=stored;
        }
        var entries=stackalloc BindGroupEntry[2];entries[0]=new(){Binding=0,TextureView=source};entries[1]=new(){Binding=1,Sampler=Sampler};
        var groupDescriptor=new BindGroupDescriptor {Layout=blitLayout,EntryCount=2,Entries=entries};var group=Api.DeviceCreateBindGroup(Handle,&groupDescriptor);
        var encoder=Encoder();var attachment=new RenderPassColorAttachment {View=destination,ResolveTarget=resolve,LoadOp=LoadOp.Clear,StoreOp=StoreOp.Store};
        var passDescriptor=new RenderPassDescriptor {ColorAttachmentCount=1,ColorAttachments=&attachment};var pass=Api.CommandEncoderBeginRenderPass(encoder,&passDescriptor);
        Api.RenderPassEncoderSetPipeline(pass,(RenderPipeline*)stored);Api.RenderPassEncoderSetBindGroup(pass,0,group,0,null);Api.RenderPassEncoderDraw(pass,3,1,0,0);
        Api.RenderPassEncoderEnd(pass);Api.RenderPassEncoderRelease(pass);Api.BindGroupRelease(group);Submit(encoder);
    }
    internal void Present(WgpuTarget target) => Present(target,target.Width,target.Height);
    /// <summary>Configures the surface to the window's framebuffer and scales the target onto it.</summary>
    internal void Present(WgpuTarget target,int surfaceWidth,int surfaceHeight) {
        RequireLive();target.Flush();if(surface==null)throw new InvalidOperationException("This wgpu device has no window surface.");
        if(surfaceWidth<=0||surfaceHeight<=0)return;
        if(configuration.Width!=surfaceWidth||configuration.Height!=surfaceHeight) {
            configuration.Width=(uint)surfaceWidth;configuration.Height=(uint)surfaceHeight;
            fixed(SurfaceConfiguration* config=&configuration)Api.SurfaceConfigure(surface,config);
        }
        SurfaceTexture frame=default;Api.SurfaceGetCurrentTexture(surface,&frame);
        if(frame.Status is SurfaceGetCurrentTextureStatus.Outdated or SurfaceGetCurrentTextureStatus.Lost) {
            if(frame.Texture!=null)Api.TextureRelease(frame.Texture);
            fixed(SurfaceConfiguration* config=&configuration)Api.SurfaceConfigure(surface,config);
            frame=default;Api.SurfaceGetCurrentTexture(surface,&frame);
        }
        if(frame.Status==SurfaceGetCurrentTextureStatus.Timeout){if(frame.Texture!=null)Api.TextureRelease(frame.Texture);return;}
        if(frame.Status!=SurfaceGetCurrentTextureStatus.Success||frame.Texture==null)throw new InvalidOperationException("wgpu presentation failed: "+frame.Status);
        var view=Api.TextureCreateView(frame.Texture,(TextureViewDescriptor*)null);
        try {Blit(target.Color.View,view,null,configuration.Format,1);Api.SurfacePresent(surface);Check();}
        finally {Api.TextureViewRelease(view);Api.TextureRelease(frame.Texture);ReleaseRetired();}
    }
    // Cached bind groups/uniform buffers can be referenced by several targets' open passes.
    // Retire replaced ones here and release them only after every target has submitted.
    private readonly List<nint> retiredGroups=new(),retiredBuffers=new();
    internal void Retire(BindGroup* group,GpuBuffer* buffer) {if(group!=null)retiredGroups.Add((nint)group);if(buffer!=null)retiredBuffers.Add((nint)buffer);}
    internal void ReleaseRetired() {
        if(retiredGroups.Count==0&&retiredBuffers.Count==0)return;Flush();
        foreach(var g in retiredGroups)Api.BindGroupRelease((BindGroup*)g);retiredGroups.Clear();
        foreach(var b in retiredBuffers)Api.BufferRelease((GpuBuffer*)b);retiredBuffers.Clear();
    }
    private BindGroup* identityGroup;private GpuBuffer* identityUniforms;
    /// <summary>Group 0 for WGPU-module draws: identity Processing matrices, the white texture and the shared sampler.</summary>
    internal BindGroup* IdentityGroup() {
        if(identityGroup!=null)return identityGroup;
        var uniforms=new float[WgpuShaders.UniformBytes/4];for(int m=0;m<4;m++)for(int i=0;i<4;i++)uniforms[m*16+i*5]=1;
        identityUniforms=Floats(uniforms,BufferUsage.Uniform);
        var entries=stackalloc BindGroupEntry[3];entries[0]=new(){Binding=0,Buffer=identityUniforms,Size=WgpuShaders.UniformBytes};entries[1]=new(){Binding=1,TextureView=White.View};entries[2]=new(){Binding=2,Sampler=Sampler};
        var descriptor=new BindGroupDescriptor {Layout=DrawingLayout,EntryCount=3,Entries=entries};identityGroup=Api.DeviceCreateBindGroup(Handle,&descriptor);Check();return identityGroup;
    }
    public void Dispose() {
        if(Disposed)return;
        foreach(var resource in Resources.ToArray())resource.Dispose();Resources.Clear();
        if(Handle!=null)Extension?.DevicePoll(Handle,true,null);
        foreach(var g in retiredGroups)Api.BindGroupRelease((BindGroup*)g);retiredGroups.Clear();
        foreach(var b in retiredBuffers)Api.BufferRelease((GpuBuffer*)b);retiredBuffers.Clear();
        if(identityGroup!=null){Api.BindGroupRelease(identityGroup);identityGroup=null;}
        if(identityUniforms!=null){Api.BufferRelease(identityUniforms);identityUniforms=null;}
        if(surface!=null){Api.SurfaceRelease(surface);surface=null;}
        White?.Dispose();
        foreach(var pipeline in blitPipelines.Values)Api.RenderPipelineRelease((RenderPipeline*)pipeline);
        if(blitShader!=null)Api.ShaderModuleRelease(blitShader);
        if(Sampler!=null)Api.SamplerRelease(Sampler);
        if(DrawingPipelineLayout!=null)Api.PipelineLayoutRelease(DrawingPipelineLayout);
        if(blitPipelineLayout!=null)Api.PipelineLayoutRelease(blitPipelineLayout);
        if(DrawingLayout!=null)Api.BindGroupLayoutRelease(DrawingLayout);
        if(UserLayout!=null)Api.BindGroupLayoutRelease(UserLayout);
        if(blitLayout!=null)Api.BindGroupLayoutRelease(blitLayout);
        if(Queue!=null)Api.QueueRelease(Queue);
        if(Handle!=null){Api.DeviceDestroy(Handle);Api.DeviceRelease(Handle);Handle=null;}
        if(adapter!=null)Api.AdapterRelease(adapter);
        if(instance!=null)Api.InstanceRelease(instance);
        // Both Api and the extension borrow the process-wide native context.
        Disposed=true;GC.KeepAlive(errorCallback);
    }
}

internal unsafe sealed class WgpuTexture : IDisposable {
    internal readonly WgpuDevice Device;
    internal Texture* Handle;
    internal TextureView* View;
    internal readonly int Width,Height;
    internal WgpuTextureDescriptor? Descriptor;
    internal WgpuTarget? SourceTarget;
    internal bool Disposed=>Handle==null;
    internal WgpuTexture(WgpuDevice device,WgpuTextureDescriptor descriptor,byte[] bytes) {
        descriptor.Validate(bytes);Device=device;Width=descriptor.Width;Height=descriptor.Height;Descriptor=descriptor;device.RequireLive();
        try {
            var desc=new TextureDescriptor {Dimension=TextureDimension.Dimension2D,Size=new(){Width=(uint)Width,Height=(uint)Height,DepthOrArrayLayers=(uint)descriptor.Layers},
                Format=descriptor.Format switch {"r8unorm"=>TextureFormat.R8Unorm,"rgba16float"=>TextureFormat.Rgba16float,"rgba8unorm-srgb"=>TextureFormat.Rgba8UnormSrgb,_=>TextureFormat.Rgba8Unorm},
                MipLevelCount=(uint)descriptor.MipLevels,SampleCount=1,Usage=TextureUsage.TextureBinding|TextureUsage.CopyDst};
            Handle=device.Api.DeviceCreateTexture(device.Handle,&desc);device.Check();
            var view=new TextureViewDescriptor {Format=desc.Format,Dimension=descriptor.Dimension=="cube"?TextureViewDimension.DimensionCube:TextureViewDimension.Dimension2D,MipLevelCount=desc.MipLevelCount,ArrayLayerCount=(uint)descriptor.Layers,Aspect=TextureAspect.All};
            View=device.Api.TextureCreateView(Handle,&view);device.Check();
            int offset=0;
            for(int mip=0;mip<descriptor.MipLevels;mip++) {
                int w=Math.Max(1,Width>>mip),h=Math.Max(1,Height>>mip),size=w*h*descriptor.Layers*descriptor.BytesPerPixel;
                var copy=new ImageCopyTexture {Texture=Handle,MipLevel=(uint)mip,Aspect=TextureAspect.All};var layout=new TextureDataLayout {BytesPerRow=(uint)(w*descriptor.BytesPerPixel),RowsPerImage=(uint)h};var extent=new Extent3D((uint)w,(uint)h,(uint)descriptor.Layers);
                fixed(byte* data=bytes)device.Api.QueueWriteTexture(device.Queue,&copy,data+offset,(nuint)size,&layout,&extent);offset+=size;
            }
            device.Check();device.Resources.Add(this);
        } catch {Dispose();throw;}
    }
    internal WgpuTexture(WgpuDevice device,int width,int height,TextureFormat format=TextureFormat.Rgba8Unorm,uint samples=1) {
        Device=device;Width=width;Height=height;
        if(format is TextureFormat.Rgba8Unorm or TextureFormat.Rgba16float)Descriptor=new(width,height,format==TextureFormat.Rgba16float?"rgba16float":"rgba8unorm","2d",1);
        if(width<=0||height<=0||width>8192||height>8192||(long)width*height>16*1024*1024)throw new ArgumentException("Texture dimensions must be 1..8192 with at most 16 million pixels.");
        device.RequireLive();
        var descriptor=new TextureDescriptor {Dimension=TextureDimension.Dimension2D,Size=new(){Width=(uint)width,Height=(uint)height,DepthOrArrayLayers=1},
            Format=format,MipLevelCount=1,SampleCount=samples,Usage=TextureUsage.RenderAttachment };
        if(samples==1&&format is TextureFormat.Rgba8Unorm or TextureFormat.Rgba16float)descriptor.Usage|=TextureUsage.TextureBinding|TextureUsage.CopySrc|TextureUsage.CopyDst;
        Handle=device.Api.DeviceCreateTexture(device.Handle,&descriptor);device.Check();View=device.Api.TextureCreateView(Handle,(TextureViewDescriptor*)null);
        device.Resources.Add(this);
    }
    /// <summary>Queue writes run before the next submission. Flush recorded draws first unless this texture has never been bound,
    /// so earlier draws keep sampling the old contents; a fresh texture can be filled without ending the current pass.</summary>
    internal void Upload(byte[] bytes,bool unused=false) {
        if(Handle==null)throw new InvalidOperationException("wgpu texture has been disposed.");
        if(bytes.Length!=checked(Width*Height*4))throw new ArgumentException("Texture upload needs width*height*4 RGBA bytes.");
        if(!unused)Device.Flush();var copy=new ImageCopyTexture {Texture=Handle,Aspect=TextureAspect.All};
        var layout=new TextureDataLayout {BytesPerRow=(uint)Width*4,RowsPerImage=(uint)Height};var extent=new Extent3D((uint)Width,(uint)Height,1);
        fixed(byte* data=bytes)Device.Api.QueueWriteTexture(Device.Queue,&copy,data,(nuint)bytes.Length,&layout,&extent);Device.Check();
    }
    public void Dispose() {if(Handle==null)return;Device.Flush();Device.Api.TextureViewRelease(View);Device.Api.TextureRelease(Handle);View=null;Handle=null;Device.Resources.Remove(this);}
}

internal unsafe sealed class WgpuShader : IDisposable {
    internal readonly WgpuDevice Device;
    internal ShaderModule* Vertex;
    internal ShaderModule* Fragment;
    internal readonly WgpuUniformLayout Uniforms;
    internal readonly WgpuTextureLayout TextureFields;
    internal readonly Dictionary<string,WgpuTexture> Textures=new();
    private BindGroupLayout* textureLayout;
    private PipelineLayout* pipelineLayout;
    private Sampler* textureSampler;
    internal readonly Dictionary<string,WgpuBuffer> Buffers=new();
    private readonly Dictionary<(int,int,int,uint,int,TextureFormat,int,bool),nint> pipelines=new();
    private BindGroup* userGroup;private GpuBuffer* userBuffer;private int userVersion=-1;
    internal bool Disposed;
    internal WgpuShader(WgpuDevice device,string vertex,string fragment) {
        Device=device;Uniforms=new(vertex+"\n"+fragment);TextureFields=new(vertex+"\n"+fragment);device.RequireLive();
        try {CreateTextureLayout();Vertex=device.Shader(WgpuShaders.Header+vertex);Fragment=device.Shader(WgpuShaders.Header+fragment);device.Resources.Add(this);}
        catch {Dispose();throw;}
    }
    private void CreateTextureLayout() {
        var entries=stackalloc BindGroupLayoutEntry[TextureFields.Fields.Count+TextureFields.Buffers.Count+1];int i=0;
        foreach(var field in TextureFields.Fields.Values)entries[i++]=new(){Binding=(uint)field.Binding,Visibility=ShaderStage.Vertex|ShaderStage.Fragment,Texture=new(){SampleType=TextureSampleType.Float,ViewDimension=field.Dimension=="cube"?TextureViewDimension.DimensionCube:TextureViewDimension.Dimension2D}};
        foreach(int binding in TextureFields.Buffers.Values)entries[i++]=new(){Binding=(uint)binding,Visibility=ShaderStage.Vertex|ShaderStage.Fragment,Buffer=new(){Type=BufferBindingType.ReadOnlyStorage}};
        entries[i]=new(){Binding=8,Visibility=ShaderStage.Vertex|ShaderStage.Fragment,Sampler=new(){Type=SamplerBindingType.Filtering}};
        var desc=new BindGroupLayoutDescriptor {EntryCount=(nuint)(i+1),Entries=entries};textureLayout=Device.Api.DeviceCreateBindGroupLayout(Device.Handle,&desc);
        var layouts=stackalloc BindGroupLayout*[3]{Device.DrawingLayout,Device.UserLayout,textureLayout};var pipeline=new PipelineLayoutDescriptor {BindGroupLayoutCount=3,BindGroupLayouts=layouts};pipelineLayout=Device.Api.DeviceCreatePipelineLayout(Device.Handle,&pipeline);
        var sampler=new SamplerDescriptor {AddressModeU=AddressMode.ClampToEdge,AddressModeV=AddressMode.ClampToEdge,AddressModeW=AddressMode.ClampToEdge,MagFilter=FilterMode.Linear,MinFilter=FilterMode.Linear,MipmapFilter=MipmapFilterMode.Linear,MaxAnisotropy=1,LodMaxClamp=32};textureSampler=Device.Api.DeviceCreateSampler(Device.Handle,&sampler);Device.Check();
    }
    internal void SetTexture(string name,WgpuTexture texture) {
        Device.RequireLive();if(Disposed||texture.Disposed)throw new InvalidOperationException("Shader or texture is disposed.");
        if(texture.Device!=Device||!TextureFields.Fields.TryGetValue(name,out var field)||texture.Descriptor?.Dimension!=field.Dimension)throw new ArgumentException("Unknown texture name, foreign device or incompatible texture dimension.");
        Textures[name]=texture;
    }
    internal void SetBuffer(string name,WgpuBuffer buffer) {
        Device.RequireLive();if(Disposed)throw new InvalidOperationException("Shader is disposed.");buffer.RequireLive();
        if(buffer.Device!=Device||!TextureFields.Buffers.ContainsKey(name))throw new ArgumentException("Unknown storage buffer name or foreign device.");
        Buffers[name]=buffer;
    }
    internal BindGroup* TextureGroup() {
        var entries=stackalloc BindGroupEntry[TextureFields.Fields.Count+TextureFields.Buffers.Count+1];int i=0;
        foreach(var pair in TextureFields.Fields) {
            if(!Textures.TryGetValue(pair.Key,out var t)||t.Disposed)throw new InvalidOperationException("Missing or disposed shader texture: "+pair.Key);
            entries[i++]=new(){Binding=(uint)pair.Value.Binding,TextureView=t.View};
        }
        foreach(var pair in TextureFields.Buffers) {
            if(!Buffers.TryGetValue(pair.Key,out var b)||b.Handle==null)throw new InvalidOperationException("Missing or disposed shader storage buffer: "+pair.Key);
            entries[i++]=new(){Binding=(uint)pair.Value,Buffer=b.Handle,Size=(ulong)b.Length*4};
        }
        entries[i]=new(){Binding=8,Sampler=textureSampler};var desc=new BindGroupDescriptor {Layout=textureLayout,EntryCount=(nuint)(i+1),Entries=entries};var group=Device.Api.DeviceCreateBindGroup(Device.Handle,&desc);Device.Check();return group;
    }
    internal void PrepareTextures(WgpuTarget destination) {
        foreach(var texture in Textures.Values)if(texture.SourceTarget is WgpuTarget source) {
            if(source==destination)throw new ArgumentException("A render target cannot sample its own color attachment.");
            source.Flush();
        }
    }
    /// <summary>User uniforms uploaded once per Set version and shared by later draws.</summary>
    internal BindGroup* UserGroup() {
        if(userGroup!=null&&userVersion==Uniforms.Version)return userGroup;
        Device.Retire(userGroup,userBuffer);userGroup=null;userBuffer=null;
        userBuffer=Device.Buffer(1024,BufferUsage.Uniform,Uniforms.Bytes);
        var entry=new BindGroupEntry {Binding=0,Buffer=userBuffer,Size=1024};var descriptor=new BindGroupDescriptor {Layout=Device.UserLayout,EntryCount=1,Entries=&entry};
        userGroup=Device.Api.DeviceCreateBindGroup(Device.Handle,&descriptor);Device.Check();userVersion=Uniforms.Version;return userGroup;
    }
    internal RenderPipeline* Pipeline(int mode,int blend,bool depth,uint samples,int stencilMode=0,TextureFormat format=TextureFormat.Rgba8Unorm) =>
        Pipeline(mode,blend,depth?1:0,samples,stencilMode,format,0,true);
    /// <summary>depthMode: 0 none, 1 test+write, 2 test only. cull: 0 none, 1 back, 2 front.</summary>
    internal RenderPipeline* Pipeline(int mode,int blend,int depthMode,uint samples,int stencilMode,TextureFormat format,int cull,bool vertexBuffer) {
        if(Disposed)throw new InvalidOperationException("wgpu shader has been disposed.");
        var key=(mode,blend,depthMode,samples,stencilMode,format,cull,vertexBuffer);
        if(pipelines.TryGetValue(key,out var stored))return (RenderPipeline*)stored;
        bool depth=depthMode!=0;
        var api=Device.Api;
        var attributes=stackalloc VertexAttribute[4];
        attributes[0]=new(){ShaderLocation=0,Offset=0,Format=VertexFormat.Float32x3};attributes[1]=new(){ShaderLocation=1,Offset=12,Format=VertexFormat.Float32x3};
        attributes[2]=new(){ShaderLocation=2,Offset=24,Format=VertexFormat.Float32x2};attributes[3]=new(){ShaderLocation=3,Offset=32,Format=VertexFormat.Float32x4};
        var buffer=new VertexBufferLayout {ArrayStride=48,StepMode=VertexStepMode.Vertex,AttributeCount=4,Attributes=attributes};
        var blending=new BlendState {Color=new(){Operation=BlendOperation.Add,SrcFactor=blend switch {2=>BlendFactor.Dst,3 or 4 or 5=>BlendFactor.One,_=>BlendFactor.SrcAlpha},
            DstFactor=blend switch {1=>BlendFactor.One,2 or 4=>BlendFactor.Zero,3=>BlendFactor.OneMinusSrc,_=>BlendFactor.OneMinusSrcAlpha}},
            Alpha=new(){Operation=BlendOperation.Add,SrcFactor=BlendFactor.One,DstFactor=blend switch{1=>BlendFactor.One,4=>BlendFactor.Zero,_=>BlendFactor.OneMinusSrcAlpha}} };
        var color=new ColorTargetState {Format=format,WriteMask=stencilMode is 1 or 2?ColorWriteMask.None:ColorWriteMask.All,Blend=&blending};
        byte* vs=stackalloc byte[]{(byte)'v',(byte)'s',(byte)'_',(byte)'m',(byte)'a',(byte)'i',(byte)'n',0};byte* fs=stackalloc byte[]{(byte)'f',(byte)'s',(byte)'_',(byte)'m',(byte)'a',(byte)'i',(byte)'n',0};
        var fragment=new FragmentState {Module=Fragment,EntryPoint=fs,TargetCount=1,Targets=&color};
        var stencil=new StencilFaceState {Compare=stencilMode==3?CompareFunction.Equal:CompareFunction.Always,FailOp=StencilOperation.Keep,DepthFailOp=StencilOperation.Keep,PassOp=stencilMode is 1 or 2?StencilOperation.Replace:StencilOperation.Keep};
        var depthState=new DepthStencilState {Format=TextureFormat.Depth24PlusStencil8,DepthWriteEnabled=depthMode==1&&stencilMode is not (1 or 2),DepthCompare=depth&&stencilMode is not (1 or 2)?CompareFunction.LessEqual:CompareFunction.Always,
            StencilFront=stencil,StencilBack=stencil,StencilReadMask=255,StencilWriteMask=stencilMode is 1 or 2?255u:0u};
        var descriptor=new RenderPipelineDescriptor {Layout=pipelineLayout,Vertex=new(){Module=Vertex,EntryPoint=vs,BufferCount=vertexBuffer?1u:0u,Buffers=vertexBuffer?&buffer:null},Fragment=&fragment,
            Primitive=new(){Topology=mode switch{0=>PrimitiveTopology.PointList,1=>PrimitiveTopology.LineList,5=>PrimitiveTopology.TriangleStrip,_=>PrimitiveTopology.TriangleList},FrontFace=FrontFace.Ccw,CullMode=cull switch{1=>CullMode.Back,2=>CullMode.Front,_=>CullMode.None}},
            DepthStencil=&depthState,Multisample=new(){Count=samples,Mask=uint.MaxValue} };
        var pipeline=api.DeviceCreateRenderPipeline(Device.Handle,&descriptor);
        try {Device.Check();}catch {if(pipeline!=null)api.RenderPipelineRelease(pipeline);throw;}
        if(pipeline==null)throw new InvalidOperationException("wgpu render pipeline creation failed.");
        pipelines[key]=(nint)pipeline;return pipeline;
    }
    public void Dispose() {if(Disposed)return;Device.Flush();Disposed=true;Device.Retire(userGroup,userBuffer);userGroup=null;userBuffer=null;foreach(var pipeline in pipelines.Values)Device.Api.RenderPipelineRelease((RenderPipeline*)pipeline);if(Vertex!=null)Device.Api.ShaderModuleRelease(Vertex);if(Fragment!=null)Device.Api.ShaderModuleRelease(Fragment);if(pipelineLayout!=null)Device.Api.PipelineLayoutRelease(pipelineLayout);if(textureLayout!=null)Device.Api.BindGroupLayoutRelease(textureLayout);if(textureSampler!=null)Device.Api.SamplerRelease(textureSampler);Device.Resources.Remove(this);}
}

internal unsafe sealed class WgpuTarget : IDisposable {
    internal readonly WgpuDevice Device;
    internal WgpuTexture Color;
    internal readonly TextureFormat ColorFormat;
    internal static TextureFormat Format(string format)=>format switch {"rgba8unorm"=>TextureFormat.Rgba8Unorm,"rgba16float"=>TextureFormat.Rgba16float,_=>throw new ArgumentException("Render target format must be rgba8unorm or rgba16float.")};
    private WgpuTexture depth;
    private WgpuTexture? multisample;
    internal int Width=>Color.Width;
    internal int Height=>Color.Height;
    private uint samples=1;
    private CommandEncoder* encoder;
    private RenderPassEncoder* pass;
    private readonly List<nint> pendingBuffers=new();
    private readonly List<nint> pendingGroups=new();
    internal bool Disposed;
    internal WgpuTarget(WgpuDevice device,int width,int height,TextureFormat format=TextureFormat.Rgba8Unorm) {
        Device=device;ColorFormat=format;Color=new(device,width,height,format){SourceTarget=this};depth=new(device,width,height,TextureFormat.Depth24PlusStencil8);
        device.Resources.Add(this);Clear(Vector4.Zero);
    }
    internal void Configure(int width,int height,bool smooth) {
        if(Disposed)throw new InvalidOperationException("wgpu render target has been disposed.");
        Device.RequireLive();uint wanted=smooth?4u:1u;
        if(width!=Width||height!=Height) {
            Flush();Color.Dispose();depth.Dispose();multisample?.Dispose();multisample=null;samples=1;
            Color=new(Device,width,height,ColorFormat){SourceTarget=this};depth=new(Device,width,height,TextureFormat.Depth24PlusStencil8);Clear(Vector4.Zero);
        }
        if(samples==wanted)return;Flush();depth.Dispose();multisample?.Dispose();multisample=null;samples=wanted;
        depth=new(Device,width,height,TextureFormat.Depth24PlusStencil8,samples);
        if(samples==4){multisample=new(Device,width,height,ColorFormat,4);Device.Blit(Color.View,multisample.View,null,ColorFormat,4);}
        Begin(null,true);
    }
    private void Begin(Vector4? clear=null,bool clearDepth=false,bool clearStencil=false) {
        if(pass!=null)return;
        encoder=Device.Encoder();var color=new RenderPassColorAttachment {View=multisample==null?Color.View:multisample.View,ResolveTarget=multisample==null?null:Color.View,
            LoadOp=clear.HasValue?LoadOp.Clear:LoadOp.Load,StoreOp=StoreOp.Store,ClearValue=clear is Vector4 c?new GpuColor(c.X*c.W,c.Y*c.W,c.Z*c.W,c.W):default };
        var depthAttachment=new RenderPassDepthStencilAttachment {View=depth.View,DepthLoadOp=clear.HasValue||clearDepth?LoadOp.Clear:LoadOp.Load,DepthStoreOp=StoreOp.Store,DepthClearValue=1,
            StencilLoadOp=clear.HasValue||clearDepth||clearStencil?LoadOp.Clear:LoadOp.Load,StencilStoreOp=StoreOp.Store };
        var descriptor=new RenderPassDescriptor {ColorAttachmentCount=1,ColorAttachments=&color,DepthStencilAttachment=&depthAttachment};pass=Device.Api.CommandEncoderBeginRenderPass(encoder,&descriptor);
    }
    /// <summary>Pipeline state for WGPU-module draws; Processing canvases keep their own per-draw state.</summary>
    internal WgpuDrawState State=WgpuDrawState.Default;
    internal void Clear(Vector4 color) {Flush();Begin(color);}
    /// <summary>Draws retained vertices (vertexBuffer) or attribute-free procedural vertices (null).</summary>
    internal void DrawRetained(WgpuShader shader,WgpuVertexBuffer? vertices,int first,int count) {
        if(shader.Device!=Device||vertices!=null&&vertices.Device!=Device)throw new ArgumentException("wgpu resources must belong to the same device.");
        vertices?.RequireLive();shader.PrepareTextures(this);
        var pipeline=shader.Pipeline(4,State.BlendCode,State.DepthCode,samples,0,ColorFormat,State.CullCode,vertices!=null);Begin();var api=Device.Api;
        var group=Device.IdentityGroup();var userGroup=shader.UserGroup();
        api.RenderPassEncoderSetPipeline(pass,pipeline);api.RenderPassEncoderSetBindGroup(pass,0,group,0,null);api.RenderPassEncoderSetBindGroup(pass,1,userGroup,0,null);
        var textureGroup=shader.TextureGroup();pendingGroups.Add((nint)textureGroup);api.RenderPassEncoderSetBindGroup(pass,2,textureGroup,0,null);
        if(vertices!=null)api.RenderPassEncoderSetVertexBuffer(pass,0,vertices.Handle,0,(ulong)vertices.Length*4);
        api.RenderPassEncoderSetScissorRect(pass,0,0,(uint)Width,(uint)Height);
        api.RenderPassEncoderDraw(pass,(uint)count,1,(uint)first,0);
        Device.Check();
    }
    internal void ClearStencil() {Flush();Begin(clearStencil:true);}
    internal void Draw(WgpuShader shader,float[] vertices,float[] uniforms,int mode,int blend,bool threeD,Vector4? clip,WgpuTexture? texture=null,int stencilMode=0,int depthMode=-1,int cull=0) {
        if(shader.Device!=Device||texture!=null&&texture.Device!=Device)throw new ArgumentException("wgpu resources must belong to the same device.");
        if(vertices.Length==0||vertices.Length%12!=0)throw new ArgumentException("Vertices need 12 floats each: position, normal, UV, RGBA.");
        shader.PrepareTextures(this);
        var pipeline=depthMode<0?shader.Pipeline(mode,blend,threeD,samples,stencilMode,ColorFormat):shader.Pipeline(mode,blend,depthMode,samples,stencilMode,ColorFormat,cull,true);
        Begin();var api=Device.Api;
        var vertex=Device.Floats(vertices,BufferUsage.Vertex);pendingBuffers.Add((nint)vertex);
        var uniform=Device.Floats(uniforms,BufferUsage.Uniform);pendingBuffers.Add((nint)uniform);
        var user=Device.Buffer(1024,BufferUsage.Uniform,shader.Uniforms.Bytes);pendingBuffers.Add((nint)user);
        var entries=stackalloc BindGroupEntry[3];entries[0]=new(){Binding=0,Buffer=uniform,Size=WgpuShaders.UniformBytes};entries[1]=new(){Binding=1,TextureView=(texture??Device.White).View};entries[2]=new(){Binding=2,Sampler=Device.Sampler};
        var descriptor=new BindGroupDescriptor {Layout=Device.DrawingLayout,EntryCount=3,Entries=entries};var group=api.DeviceCreateBindGroup(Device.Handle,&descriptor);pendingGroups.Add((nint)group);
        var userEntry=new BindGroupEntry {Binding=0,Buffer=user,Size=1024};descriptor=new(){Layout=Device.UserLayout,EntryCount=1,Entries=&userEntry};var userGroup=api.DeviceCreateBindGroup(Device.Handle,&descriptor);pendingGroups.Add((nint)userGroup);
        api.RenderPassEncoderSetPipeline(pass,pipeline);api.RenderPassEncoderSetBindGroup(pass,0,group,0,null);api.RenderPassEncoderSetBindGroup(pass,1,userGroup,0,null);
        var textureGroup=shader.TextureGroup();pendingGroups.Add((nint)textureGroup);api.RenderPassEncoderSetBindGroup(pass,2,textureGroup,0,null);
        api.RenderPassEncoderSetStencilReference(pass,stencilMode==2?0u:1u);
        api.RenderPassEncoderSetVertexBuffer(pass,0,vertex,0,(ulong)vertices.Length*4);
        var bounds=Scissor(Width,Height,clip);api.RenderPassEncoderSetScissorRect(pass,(uint)bounds.X,(uint)bounds.Y,(uint)bounds.Width,(uint)bounds.Height);
        if(bounds.Width>0&&bounds.Height>0)api.RenderPassEncoderDraw(pass,(uint)(vertices.Length/12),1,0,0);
        Device.Check();
    }
    internal static (int X,int Y,int Width,int Height) Scissor(int width,int height,Vector4? clip) {
        if(clip==null)return(0,0,width,height);var c=clip.Value;
        int x=(int)Math.Clamp(c.X,0,width),y=(int)Math.Clamp(c.Y,0,height),right=(int)Math.Clamp(c.X+c.Z,0,width),bottom=(int)Math.Clamp(c.Y+c.W,0,height);
        return(x,y,Math.Max(0,right-x),Math.Max(0,bottom-y));
    }
    internal void Flush() {
        if(pass==null)return;Device.Api.RenderPassEncoderEnd(pass);Device.Api.RenderPassEncoderRelease(pass);pass=null;
        var commands=encoder;encoder=null;
        try {Device.Submit(commands);}
        finally {
            // wgpu-native resolves recorded resource IDs at pass end; keep them alive until submission.
            foreach(var group in pendingGroups)Device.Api.BindGroupRelease((BindGroup*)group);pendingGroups.Clear();
            foreach(var buffer in pendingBuffers)Device.Api.BufferRelease((GpuBuffer*)buffer);pendingBuffers.Clear();
        }
    }
    internal byte[] ReadPixels() {
        if(ColorFormat!=TextureFormat.Rgba8Unorm)throw new InvalidOperationException("Tone-map HDR targets into rgba8unorm before reading pixels.");
        if(Disposed)throw new InvalidOperationException("wgpu render target has been disposed.");Device.Flush();
        int row=checked((Width*4+255)/256*256),size=checked(row*Height);
        var descriptor=new BufferDescriptor {Size=(ulong)size,Usage=BufferUsage.CopyDst|BufferUsage.MapRead};var buffer=Device.Api.DeviceCreateBuffer(Device.Handle,&descriptor);
        try {
            var copy=new ImageCopyTexture {Texture=Color.Handle,Aspect=TextureAspect.All};var output=new ImageCopyBuffer {Buffer=buffer,Layout=new(){BytesPerRow=(uint)row,RowsPerImage=(uint)Height}};
            var extent=new Extent3D((uint)Width,(uint)Height,1);var commands=Device.Encoder();Device.Api.CommandEncoderCopyTextureToBuffer(commands,&copy,&output,&extent);Device.Submit(commands);
            var padded=Device.Map(buffer,size);var bytes=new byte[Width*Height*4];for(int y=0;y<Height;y++)System.Array.Copy(padded,y*row,bytes,y*Width*4,Width*4);
            for(int i=0;i<bytes.Length;i+=4)if(bytes[i+3]>0&&bytes[i+3]<255)for(int c=0;c<3;c++)bytes[i+c]=(byte)Math.Min(255,(bytes[i+c]*255+bytes[i+3]/2)/bytes[i+3]);
            return bytes;
        } finally {Device.Api.BufferRelease(buffer);}
    }
    public void Dispose() {if(Disposed)return;Flush();Disposed=true;Color.Dispose();depth.Dispose();multisample?.Dispose();Device.Resources.Remove(this);}
}
