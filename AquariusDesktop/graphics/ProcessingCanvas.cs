using System.Numerics;
using AquariusLang.Graphics;
using AquariusLang.Object;

namespace AquariusLang.Desktop.Graphics;

internal sealed partial class ProcessingCanvas : IDisposable {
    internal WgpuDevice Device=null!;
    internal WgpuTarget Target=null!;
    private WgpuShader? program;
    internal readonly GraphicsRuntime Owner;
    internal GraphicsSurfaceSize Resolution;
    internal int Width => Resolution.Width;
    internal int Height => Resolution.Height;
    internal int PixelWidth => Resolution.PixelWidth;
    internal int PixelHeight => Resolution.PixelHeight;
    internal ProcessingImage? Surface;
    internal ProcessingStyle Style=new();
    internal Matrix4x4 Model=Matrix4x4.Identity, View=Matrix4x4.Identity, Projection;
    internal readonly Stack<Matrix4x4> Matrices=new();
    internal readonly Stack<ProcessingStyle> Styles=new();

    internal WgpuShader? CustomProgram;
    internal bool Is3D, Drawing, Disposed;
    internal int Detail=32, SphereU=24, SphereV=16, ShapeMode=-1, TextureMode;
    internal float Tightness;
    internal readonly List<ProcessingVertex> Vertices=new();
    internal readonly List<List<ProcessingVertex>> Contours=new();
    internal bool InContour;
    internal List<ProcessingVertex> ActiveVertices=>InContour?Contours[^1]:Vertices;
    internal readonly List<Vector3> CurveVertices=new();
    internal ProcessingImage? Texture;
    internal Vector3 Normal=Vector3.UnitZ;
    internal bool Smooth=true;
    internal Silk.NET.WebGPU.TextureFormat ColorFormat=Silk.NET.WebGPU.TextureFormat.Rgba8Unorm;
    internal Vector4? Clip;
    internal ModuleObj? Module;

    internal ProcessingCanvas(GraphicsRuntime owner,int w,int h,bool threeD) {
        Owner=owner; Resolution=new(w,h,w,h); Is3D=threeD; DefaultCamera();
    }
    internal void Initialize(WgpuDevice device,bool offscreen=false) {
        Device=device;
        try {
            Target=new(device,PixelWidth,PixelHeight,ColorFormat);program=new(device,WgpuShaders.Vertex,WgpuShaders.Fragment);
            if(offscreen) {
                Surface=ProcessingImage.Create(Owner,PixelWidth,PixelHeight);Surface.Canvas=this;Surface.GpuTexture=Target.Color;
                Surface.Dirty=false;Surface.Premultiplied=true;
            }
            Bind();Background(new Vector4(.8f,.8f,.8f,1));
        } catch {Dispose();throw;}
    }
    internal void RequireReadable() {
        if(Disposed) throw new InvalidOperationException("Canvas has been disposed.");
        if(ShapeMode!=-1) throw new InvalidOperationException("Finish beginShape() with endShape() before drawing another primitive.");
    }
    internal void RequireDrawing() {
        RequireReadable();
        if(!Drawing) throw new InvalidOperationException("Call size() for the main canvas, or beginDraw() for PGraphics first.");
    }
    internal void Bind() {
        if(PixelWidth<=0||PixelHeight<=0)return;
        Target.Configure(PixelWidth,PixelHeight,Smooth);
        if(Surface!=null)Surface.GpuTexture=Target.Color;
    }
    internal bool Resize(int width,int height,int pixelWidth,int pixelHeight) {
        var next=Resolution.Resize(width,height,pixelWidth,pixelHeight);
        bool logical=Width!=next.Width||Height!=next.Height;
        bool changed=Resolution!=next;
        Resolution=next;
        if(logical)DefaultCamera();
        return changed;
    }
    internal void DefaultCamera() {
        Model=Matrix4x4.Identity;
        if(Is3D) {
            float z=Height/2f/MathF.Tan(MathF.PI/6);
            View=Matrix4x4.CreateLookAt(new(Width/2f,Height/2f,z),new(Width/2f,Height/2f,0),Vector3.UnitY);
            // Processing uses downwards screen Y, including its default P3D camera.
            Projection=Perspective(MathF.PI/3,(float)Width/Height,z/10,z*10);
            Projection.M22=-Projection.M22;
        } else { View=Matrix4x4.Identity; Projection=Ortho(0,Width,Height,0,-10000,10000); }
    }
    internal static Matrix4x4 Ortho(float l,float r,float b,float t,float n,float f) {
        if(l==r||b==t||n==f) throw new ArgumentException("Orthographic bounds must have nonzero ranges.");
        return new(2/(r-l),0,0,0,0,2/(t-b),0,0,0,0,-2/(f-n),0,-(r+l)/(r-l),-(t+b)/(t-b),-(f+n)/(f-n),1);
    }
    internal static Matrix4x4 Perspective(float fov,float aspect,float near,float far) {
        if(fov<=0||fov>=MathF.PI||aspect<=0||near<=0||far<=near) throw new ArgumentException("Invalid perspective parameters.");
        float f=1/MathF.Tan(fov/2); return new(f/aspect,0,0,0,0,f,0,0,0,0,(far+near)/(near-far),-1,0,0,2*far*near/(near-far),0);
    }
    internal void Background(Vector4 c) {Bind();Target.Clear(c);}
    internal void SetClip(float x,float y,float width,float height) {
        // Capture the current drawing transform, just like the geometry being clipped.
        // Keep logical screen coordinates here; Draw applies framebuffer density once.
        var matrix=Model*View*Projection;
        var minimum=new Vector2(float.PositiveInfinity);var maximum=new Vector2(float.NegativeInfinity);
        foreach(var corner in new[]{new Vector2(x,y),new(x+width,y),new(x+width,y+height),new(x,y+height)}) {
            var point=Vector4.Transform(new Vector4(corner,0,1),matrix);
            if(point.W<=0||!float.IsFinite(point.W)) {Clip=Vector4.Zero;return;}
            var screen=new Vector2((point.X/point.W+1)*Width/2,(1-point.Y/point.W)*Height/2);
            if(!float.IsFinite(screen.X)||!float.IsFinite(screen.Y)) {Clip=Vector4.Zero;return;}
            minimum=Vector2.Min(minimum,screen);maximum=Vector2.Max(maximum,screen);
        }
        Clip=new Vector4(minimum,maximum.X-minimum.X,maximum.Y-minimum.Y);
    }
    // Packing buffers reused by every draw; the target copies them into its upload arenas before Draw returns.
    private float[] packedVertices=System.Array.Empty<float>();
    private readonly float[] packedUniforms=new float[WgpuShaders.UniformBytes/4];
    internal void Draw(IReadOnlyList<ProcessingVertex> vertices,int mode=4,ProcessingImage? image=null,bool lit=true,int stencilMode=0) {
        if(vertices.Count==0)return;
        int length=vertices.Count*GeometryKernel.FloatsPerVertex;
        if(packedVertices.Length<length)packedVertices=new float[Math.Max(length,packedVertices.Length*2)];
        var floats=packedVertices;int i=0;
        foreach(var v in vertices) {
            floats[i++]=v.Position.X;floats[i++]=v.Position.Y;floats[i++]=v.Position.Z;
            floats[i++]=v.Normal.X;floats[i++]=v.Normal.Y;floats[i++]=v.Normal.Z;
            floats[i++]=v.UV.X;floats[i++]=v.UV.Y;
            floats[i++]=v.Color.X;floats[i++]=v.Color.Y;floats[i++]=v.Color.Z;floats[i++]=v.Color.W;
        }
        DrawPacked(floats.AsSpan(0,length),mode,image,lit,stencilMode);
    }
    internal void DrawPacked(ReadOnlySpan<float> vertices,int mode=4,ProcessingImage? image=null,bool lit=true,int stencilMode=0) {
        if(vertices.Length==0)return;
        Bind();image?.Upload(this);
        Vector4? clip=Clip is Vector4 c?new Vector4(c.X*PixelWidth/Width,c.Y*PixelHeight/Height,c.Z*PixelWidth/Width,c.W*PixelHeight/Height):null;
        WgpuShaders.Uniforms(packedUniforms,this,image!=null,image?.Premultiplied==true,lit);
        Target.Draw(CustomProgram??program!,vertices,packedUniforms,mode,BlendMode,Is3D,clip,image?.GpuTexture,stencilMode);
    }
    internal ProcessingVertex V(Vector3 p,Vector4 color,Vector2 uv=default,Vector3? normal=null) => new(p,normal??Normal,uv,color);

    // ---- geometry: tessellation runs in the shared kernel (geometry.wasm); this canvas draws its vertex ranges ----
    private GeometryKernel Geometry() {
        var kernel=Owner.Geometry;var s=kernel.State;
        s[GeometryKernel.Weight]=Style.Weight;s[GeometryKernel.Cap]=Style.Cap;s[GeometryKernel.Join]=Style.Join;s[GeometryKernel.Is3D]=Is3D?1:0;
        s[GeometryKernel.Width]=Width;s[GeometryKernel.Height]=Height;
        Put(s,GeometryKernel.StrokeColor,Style.Stroke);Put(s,GeometryKernel.FillColor,Style.Fill);
        s[GeometryKernel.Normal]=Normal.X;s[GeometryKernel.Normal+1]=Normal.Y;s[GeometryKernel.Normal+2]=Normal.Z;
        s[GeometryKernel.HasFill]=Style.HasFill?1:0;s[GeometryKernel.HasStroke]=Style.HasStroke?1:0;
        s[GeometryKernel.RectMode]=Style.RectMode;s[GeometryKernel.EllipseMode]=Style.EllipseMode;s[GeometryKernel.Detail]=Detail;s[GeometryKernel.Tightness]=Tightness;
        var m=Model*View*Projection;int o=GeometryKernel.Mvp;
        s[o]=m.M11;s[o+1]=m.M12;s[o+2]=m.M13;s[o+3]=m.M14;s[o+4]=m.M21;s[o+5]=m.M22;s[o+6]=m.M23;s[o+7]=m.M24;
        s[o+8]=m.M31;s[o+9]=m.M32;s[o+10]=m.M33;s[o+11]=m.M34;s[o+12]=m.M41;s[o+13]=m.M42;s[o+14]=m.M43;s[o+15]=m.M44;
        kernel.StateChanged();return kernel;
    }
    private static void Put(Span<float> target,int offset,Vector4 c){target[offset]=c.X;target[offset+1]=c.Y;target[offset+2]=c.Z;target[offset+3]=c.W;}
    private static void Points(GeometryKernel kernel,IReadOnlyList<Vector3> points) {
        var input=kernel.Input(points.Count*3);
        for(int i=0;i<points.Count;i++){input[i*3]=points[i].X;input[i*3+1]=points[i].Y;input[i*3+2]=points[i].Z;}
    }
    private static void Floats(GeometryKernel kernel,params float[] values)=>values.CopyTo(kernel.Input(values.Length));
    /// <summary>Draws the kernel's last output: lit fills, then unlit fills and strokes, in emission order.</summary>
    private void DrawGeometry(GeometryKernel kernel,ProcessingImage? image=null) {
        var vertices=kernel.Vertices;
        foreach(var range in kernel.Ranges.ToArray())
            DrawPacked(vertices.Slice(range.Start*GeometryKernel.FloatsPerVertex,range.Count*GeometryKernel.FloatsPerVertex),image:image,lit:range.Kind==GeometryKernel.RangeKind.Fill);
    }
    internal void Polygon(IReadOnlyList<Vector3> points,bool close=true,bool fill=true) {
        RequireDrawing();var kernel=Geometry();Points(kernel,points);kernel.Run("geo_polygon",points.Count,close?1:0,fill?1:0);DrawGeometry(kernel);
    }
    internal void Polyline(IReadOnlyList<Vector3> points,bool close=false) {
        if(!Style.HasStroke||points.Count<2) return;
        var kernel=Geometry();Points(kernel,points);kernel.Run("geo_polyline",points.Count,close?1:0);DrawGeometry(kernel);
    }
    internal void Segment(Vector3 a,Vector3 b) {
        if(!Style.HasStroke||Style.Weight<=0) return;
        var kernel=Geometry();Points(kernel,new[]{a,b});kernel.Run("geo_line");DrawGeometry(kernel);
    }
    internal void Point(Vector3 p) {
        if(!Style.HasStroke) return;
        var kernel=Geometry();Points(kernel,new[]{p});kernel.Run("geo_point");DrawGeometry(kernel);
    }
    /// <summary>rect()/square() in the current rect mode; a negative radius means none was given.</summary>
    internal void Rect(float x,float y,float w,float h,float radius=-1) {
        RequireDrawing();var kernel=Geometry();Floats(kernel,x,y,w,h,radius);kernel.Run("geo_rect");DrawGeometry(kernel);
    }
    internal void Ellipse(float x,float y,float w,float h,float start=0,float stop=MathF.Tau,int mode=2,bool arc=false) {
        RequireDrawing();var kernel=Geometry();Floats(kernel,x,y,w,h,start,stop);kernel.Run("geo_ellipse",mode,arc?1:0);DrawGeometry(kernel);
    }
    /// <summary>bezier() or (catmull) curve() through four control points, as a stroke of Detail segments.</summary>
    internal void Curve(bool catmull,IReadOnlyList<Vector3> controls) {
        var kernel=Geometry();Points(kernel,controls);kernel.Run("geo_curve",catmull?1:0);DrawGeometry(kernel);
    }
    /// <summary>Detail samples of a bezier (or catmull curve) through four control points, from sample `from`.</summary>
    internal List<Vector3> CurvePoints(bool catmull,IReadOnlyList<Vector3> controls,int from) {
        var kernel=Geometry();Points(kernel,controls);int count=kernel.Run("geo_curve_points",catmull?1:0,from);
        var samples=kernel.Output(12,count*3);var result=new List<Vector3>(count);
        for(int i=0;i<count;i++)result.Add(new(samples[i*3],samples[i*3+1],samples[i*3+2]));
        return result;
    }
    internal void Image(ProcessingImage image,float x,float y,float? w=null,float? h=null) {
        RequireDrawing(); if(Surface==image) throw new ArgumentException("Cannot draw a canvas into itself.");
        var kernel=Geometry();var t=Style.Tint;
        Floats(kernel,x,y,w??image.Canvas?.Width??image.Width,h??image.Canvas?.Height??image.Height,t.X,t.Y,t.Z,t.W);
        kernel.Run("geo_image",Style.ImageMode,image.Flipped?1:0);DrawGeometry(kernel,image);
    }
    internal byte[] ReadPixels() {Bind();return Target.ReadPixels();}
    public void Dispose() {
        if(Disposed)return;Disposed=true;Target?.Dispose();program?.Dispose();Surface?.Dispose(this);
    }
}
internal sealed class ProcessingImage {
    internal readonly GraphicsRuntime Owner;
    internal int Width,Height;
    internal byte[] Bytes;
    internal WgpuTexture? GpuTexture;
    internal ProcessingCanvas? Canvas;
    internal bool Dirty=true,Flipped,Disposed,Premultiplied;
    internal ModuleObj Module;
    private ProcessingImage(GraphicsRuntime owner,int w,int h) {
        if(w<=0||h<=0||w>8192||h>8192||checked((long)w*h)>16*1024*1024) throw new ArgumentException("Image dimensions must be 1..8192 with at most 16 million pixels.");
        Owner=owner; Width=w; Height=h; Bytes=new byte[w*h*4]; Module=new ModuleObj(AquariusLang.Object.Environment.NewEnvironment());
        Module._Environment.Create("width",new IntegerObj(w)); Module._Environment.Create("height",new IntegerObj(h));
    }
    internal static ProcessingImage Create(GraphicsRuntime owner,int w,int h) => new(owner,w,h);
    internal void Upload(ProcessingCanvas canvas) {
        if(Disposed)throw new ArgumentException("Image has been disposed.");
        if(GpuTexture!=null&&(GpuTexture.Width!=Width||GpuTexture.Height!=Height)) {GpuTexture.Dispose();GpuTexture=null;}
        bool fresh=GpuTexture==null;GpuTexture??=new WgpuTexture(canvas.Device,Width,Height);
        if(!Dirty)return;
        GpuTexture.Upload(Bytes,fresh);Dirty=false;
    }
    internal void Dispose(ProcessingCanvas canvas) {if(Disposed)return;Disposed=true;GpuTexture?.Dispose();GpuTexture=null;}
}
