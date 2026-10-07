using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using GpuBuffer=Silk.NET.WebGPU.Buffer;

namespace AquariusREPL.Graphics;

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
    public void Dispose() {if(Handle==null)return;Device.Flush();Device.Api.BufferRelease(Handle);Handle=null;Device.Resources.Remove(this);}
}

internal unsafe sealed partial class WgpuDevice {
    internal void Poll() {RequireLive();Extension.DevicePoll(Handle,true,null);Check();}
    internal void Dispatch(string source,WgpuBuffer buffer,uint groups) {
        RequireLive();buffer.RequireLive();if(buffer.Device!=this)throw new ArgumentException("Buffer belongs to another wgpu device.");Flush();
        var shader=Shader(source);BindGroupLayout* layout=null;PipelineLayout* pipelineLayout=null;ComputePipeline* pipeline=null;BindGroup* group=null;
        try {
            var entry=new BindGroupLayoutEntry {Binding=0,Visibility=ShaderStage.Compute,Buffer=new(){Type=BufferBindingType.Storage}};
            var layoutDescriptor=new BindGroupLayoutDescriptor {EntryCount=1,Entries=&entry};layout=Api.DeviceCreateBindGroupLayout(Handle,&layoutDescriptor);
            var pipelineLayoutDescriptor=new PipelineLayoutDescriptor {BindGroupLayoutCount=1,BindGroupLayouts=&layout};pipelineLayout=Api.DeviceCreatePipelineLayout(Handle,&pipelineLayoutDescriptor);
            byte* name=stackalloc byte[]{(byte)'c',(byte)'s',(byte)'_',(byte)'m',(byte)'a',(byte)'i',(byte)'n',0};
            var pipelineDescriptor=new ComputePipelineDescriptor {Layout=pipelineLayout,Compute=new(){Module=shader,EntryPoint=name}};
            pipeline=Api.DeviceCreateComputePipeline(Handle,&pipelineDescriptor);Check();
            var binding=new BindGroupEntry {Binding=0,Buffer=buffer.Handle,Size=(ulong)buffer.Length*4};
            var groupDescriptor=new BindGroupDescriptor {Layout=layout,EntryCount=1,Entries=&binding};group=Api.DeviceCreateBindGroup(Handle,&groupDescriptor);Check();
            var encoder=Encoder();var pass=Api.CommandEncoderBeginComputePass(encoder,(ComputePassDescriptor*)null);
            Api.ComputePassEncoderSetPipeline(pass,pipeline);Api.ComputePassEncoderSetBindGroup(pass,0,group,0,null);Api.ComputePassEncoderDispatchWorkgroups(pass,groups,1,1);
            Api.ComputePassEncoderEnd(pass);Api.ComputePassEncoderRelease(pass);Submit(encoder);
        } finally {
            if(group!=null)Api.BindGroupRelease(group);if(pipeline!=null)Api.ComputePipelineRelease(pipeline);
            if(pipelineLayout!=null)Api.PipelineLayoutRelease(pipelineLayout);if(layout!=null)Api.BindGroupLayoutRelease(layout);Api.ShaderModuleRelease(shader);
        }
    }
}
