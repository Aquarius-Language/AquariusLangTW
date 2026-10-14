using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using GpuBuffer=Silk.NET.WebGPU.Buffer;

namespace AquariusLang.Desktop.Graphics;

internal unsafe sealed class WgpuBuffer : IDisposable {
    internal readonly WgpuDevice Device;
    internal GpuBuffer* Handle;
    internal readonly int Length;
    internal WgpuBuffer(WgpuDevice device,float[] values) {
        Device=device;Length=values.Length;Handle=device.Floats(values,BufferUsage.Storage|BufferUsage.CopySrc);device.Resources.Add(this);
    }
    internal void RequireLive() {Device.RequireLive();if(Handle==null)throw new InvalidOperationException("wgpu buffer has been disposed.");}
    internal void Write(float[] values) {
        RequireLive();if(values.Length!=Length)throw new ArgumentException($"Buffer Write needs {Length} floats.");Device.Flush();
        fixed(float* p=values)Device.Api.QueueWriteBuffer(Device.Queue,Handle,0,p,(nuint)Length*4);Device.Check();
    }
    internal float[] Read() {RequireLive();var bytes=Device.ReadBuffer(Handle,Length*4);return MemoryMarshal.Cast<byte,float>(bytes).ToArray();}
    internal void WriteRange(int offset,float[] values) {
        RequireLive();AquariusLang.Graphics.WgpuBufferRange.Validate(Length,offset,values.Length);Device.Flush();
        fixed(float* p=values)Device.Api.QueueWriteBuffer(Device.Queue,Handle,(ulong)offset*4,p,(nuint)values.Length*4);Device.Check();
    }
    internal float[] ReadRange(int offset,int count) {
        RequireLive();AquariusLang.Graphics.WgpuBufferRange.Validate(Length,offset,count);
        return MemoryMarshal.Cast<byte,float>(Device.ReadBuffer(Handle,count*4,(ulong)offset*4)).ToArray();
    }
    public void Dispose() {if(Handle==null)return;Device.Flush();Device.Api.BufferRelease(Handle);Handle=null;Device.Resources.Remove(this);}
}

internal unsafe sealed partial class WgpuDevice {
    private readonly Dictionary<string,ComputeKernel> computeKernels=new(StringComparer.Ordinal);
    private sealed class ComputeKernel : IDisposable {
        internal BindGroupLayout* Layout;
        internal ComputePipeline* Pipeline;
        private readonly WgpuDevice device;
        internal ComputeKernel(WgpuDevice device,string source) {
            this.device=device;var shader=device.Shader(source);PipelineLayout* pipelineLayout=null;
            try {
                var entry=new BindGroupLayoutEntry {Binding=0,Visibility=ShaderStage.Compute,Buffer=new(){Type=BufferBindingType.Storage}};
                var ld=new BindGroupLayoutDescriptor {EntryCount=1,Entries=&entry};Layout=device.Api.DeviceCreateBindGroupLayout(device.Handle,&ld);
                var layout=Layout;var pd=new PipelineLayoutDescriptor {BindGroupLayoutCount=1,BindGroupLayouts=&layout};pipelineLayout=device.Api.DeviceCreatePipelineLayout(device.Handle,&pd);
                byte* name=stackalloc byte[]{(byte)'c',(byte)'s',(byte)'_',(byte)'m',(byte)'a',(byte)'i',(byte)'n',0};
                var descriptor=new ComputePipelineDescriptor {Layout=pipelineLayout,Compute=new(){Module=shader,EntryPoint=name}};
                Pipeline=device.Api.DeviceCreateComputePipeline(device.Handle,&descriptor);device.Check();device.Resources.Add(this);
            } catch {Dispose();throw;}
            finally {if(pipelineLayout!=null)device.Api.PipelineLayoutRelease(pipelineLayout);device.Api.ShaderModuleRelease(shader);}
        }
        public void Dispose(){if(Pipeline!=null)device.Api.ComputePipelineRelease(Pipeline);if(Layout!=null)device.Api.BindGroupLayoutRelease(Layout);Pipeline=null;Layout=null;device.Resources.Remove(this);}
    }
    internal void Poll() {RequireLive();Extension.DevicePoll(Handle,true,null);Check();}
    internal void Dispatch(string source,WgpuBuffer buffer,uint groups) {
        RequireLive();buffer.RequireLive();if(buffer.Device!=this)throw new ArgumentException("Buffer belongs to another wgpu device.");Flush();
        if(!computeKernels.TryGetValue(source,out var kernel)) {
            if(computeKernels.Count>=32){var key=computeKernels.Keys.First();computeKernels[key].Dispose();computeKernels.Remove(key);}
            kernel=new ComputeKernel(this,source);computeKernels.Add(source,kernel);
        }
        BindGroup* group=null;
        try {
            var binding=new BindGroupEntry {Binding=0,Buffer=buffer.Handle,Size=(ulong)buffer.Length*4};
            var groupDescriptor=new BindGroupDescriptor {Layout=kernel.Layout,EntryCount=1,Entries=&binding};group=Api.DeviceCreateBindGroup(Handle,&groupDescriptor);Check();
            var encoder=Encoder();var pass=Api.CommandEncoderBeginComputePass(encoder,(ComputePassDescriptor*)null);
            Api.ComputePassEncoderSetPipeline(pass,kernel.Pipeline);Api.ComputePassEncoderSetBindGroup(pass,0,group,0,null);Api.ComputePassEncoderDispatchWorkgroups(pass,groups,1,1);
            Api.ComputePassEncoderEnd(pass);Api.ComputePassEncoderRelease(pass);Submit(encoder);
        } finally {
            if(group!=null)Api.BindGroupRelease(group);
        }
    }
}
