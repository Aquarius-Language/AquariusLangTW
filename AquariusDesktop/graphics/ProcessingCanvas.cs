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
    // Stroke tessellation emits many small triangle lists (segments, joins and round caps). While a stroke is being
    // built they share one pipeline state, so they are collected and submitted as a single draw call.
    private List<ProcessingVertex>? strokeBatch;
    private void Batched(Action build) {
        if(strokeBatch!=null){build();return;}
        strokeBatch=new List<ProcessingVertex>();
        List<ProcessingVertex> batch;
        try{build();}finally{batch=strokeBatch;strokeBatch=null;}
        if(batch.Count>0)Draw(batch,lit:false);
    }
    // Packing buffers reused by every draw; the target copies them into its upload arenas before Draw returns.
    private float[] packedVertices=System.Array.Empty<float>();
    private readonly float[] packedUniforms=new float[WgpuShaders.UniformBytes/4];
    internal void Draw(IReadOnlyList<ProcessingVertex> vertices,int mode=4,ProcessingImage? image=null,bool lit=true,int stencilMode=0) {
        if(vertices.Count==0)return;
        if(strokeBatch!=null&&mode==4&&image==null&&!lit&&stencilMode==0){strokeBatch.AddRange(vertices);return;}
        Bind();image?.Upload(this);
        int length=vertices.Count*12;
        if(packedVertices.Length<length)packedVertices=new float[Math.Max(length,packedVertices.Length*2)];
        var floats=packedVertices;int i=0;
        foreach(var v in vertices) {
            floats[i++]=v.Position.X;floats[i++]=v.Position.Y;floats[i++]=v.Position.Z;
            floats[i++]=v.Normal.X;floats[i++]=v.Normal.Y;floats[i++]=v.Normal.Z;
            floats[i++]=v.UV.X;floats[i++]=v.UV.Y;
            floats[i++]=v.Color.X;floats[i++]=v.Color.Y;floats[i++]=v.Color.Z;floats[i++]=v.Color.W;
        }
        Vector4? clip=Clip is Vector4 c?new Vector4(c.X*PixelWidth/Width,c.Y*PixelHeight/Height,c.Z*PixelWidth/Width,c.W*PixelHeight/Height):null;
        WgpuShaders.Uniforms(packedUniforms,this,image!=null,image?.Premultiplied==true,lit);
        Target.Draw(CustomProgram??program!,floats.AsSpan(0,length),packedUniforms,mode,BlendMode,Is3D,clip,image?.GpuTexture,stencilMode);
    }
    internal ProcessingVertex V(Vector3 p,Vector4 color,Vector2 uv=default,Vector3? normal=null) => new(p,normal??Normal,uv,color);
    internal void Polygon(IReadOnlyList<Vector3> points,bool close=true,bool fill=true) {
        RequireDrawing();
        if(Style.HasFill&&fill&&points.Count>=3) Draw(ProcessingGeometry.Triangulate(points).Select(i=>V(points[i],Style.Fill)).ToArray());
        if(Style.HasStroke) Polyline(points,close);
    }
    internal void Polyline(IReadOnlyList<Vector3> points,bool close=false) {
        if(!Style.HasStroke||points.Count<2) return;
        Batched(()=>PolylineGeometry(points,close));
    }
    private void PolylineGeometry(IReadOnlyList<Vector3> points,bool close) {
        if(!Is3D&&MiterStrip(points,close))return;
        int cap=Style.Cap;Style.Cap=0;
        try{for(int i=0;i<points.Count-(close?0:1);i++) Segment(points[i],points[(i+1)%points.Count]);}finally{Style.Cap=cap;}
        Vector2 Direction(Vector3 a,Vector3 b) {
            if(!Is3D)return new(b.X-a.X,b.Y-a.Y);
            var matrix=Model*View*Projection;var p=Vector4.Transform(new Vector4(a,1),matrix);var q=Vector4.Transform(new Vector4(b,1),matrix);
            if(p.W<=0||q.W<=0)return Vector2.Zero;return new((q.X/q.W-p.X/p.W)*Width/2,(q.Y/q.W-p.Y/p.W)*Height/2);
        }
        Vector3 Offset(Vector3 p,Vector2 delta)=>Is3D?StrokeOffset(p,delta.X,delta.Y):p+new Vector3(delta,0);
        for(int i=close?0:1;i<(close?points.Count:points.Count-1);i++) {
            var p=points[i];if(Style.Join==1){Disk(p,Style.Weight/2,Style.Stroke);continue;}
            var a=Direction(points[(i+points.Count-1)%points.Count],p);var b=Direction(p,points[(i+1)%points.Count]);
            if(a.LengthSquared()<1e-12f||b.LengthSquared()<1e-12f)continue;a=Vector2.Normalize(a);b=Vector2.Normalize(b);
            float turn=a.X*b.Y-a.Y*b.X;if(Math.Abs(turn)<1e-6f)continue;float sign=turn>=0?-1:1;
            var n1=new Vector2(-a.Y,a.X)*sign;var n2=new Vector2(-b.Y,b.X)*sign;var edge1=n1*Style.Weight/2;var edge2=n2*Style.Weight/2;
            var middle=Vector2.Normalize(n1+n2);float denom=Vector2.Dot(middle,n2);var miter=middle*Style.Weight/2/denom;
            Vector3 center=Style.Join==0&&miter.Length()<=Style.Weight*2?Offset(p,miter):p;
            Draw(new[]{V(Offset(p,edge1),Style.Stroke),V(center,Style.Stroke),V(Offset(p,edge2),Style.Stroke)},lit:false);
        }
        if(!close&&cap==1){Disk(points[0],Style.Weight/2,Style.Stroke);Disk(points[^1],Style.Weight/2,Style.Stroke);}
        if(!close&&cap==2) {
            var first=Direction(points[0],points[1]);var last=Direction(points[^2],points[^1]);
            if(first.LengthSquared()>0)Segment(points[0],Offset(points[0],-Vector2.Normalize(first)*Style.Weight/2));
            if(last.LengthSquared()>0)Segment(points[^1],Offset(points[^1],Vector2.Normalize(last)*Style.Weight/2));
        }
    }
    /// <summary>Builds a 2D polyline as one non-overlapping mitered strip. Separate segment quads and join
    /// triangles overlap at every vertex, so translucent strokes (hairline borders, rounded-rectangle outlines) showed
    /// darker dots there. Returns false for sharp corners beyond the miter limit; the caller then uses the general path.</summary>
    private bool MiterStrip(IReadOnlyList<Vector3> source,bool close) {
        var points=new List<Vector2>(source.Count);
        foreach(var p in source){var q=new Vector2(p.X,p.Y);if(points.Count==0||Vector2.DistanceSquared(points[^1],q)>1e-4f)points.Add(q);}
        if(close&&points.Count>2&&Vector2.DistanceSquared(points[0],points[^1])<=1e-4f)points.RemoveAt(points.Count-1);
        int n=points.Count;if(n<2||(close&&n<3))return false;
        int segments=close?n:n-1;float half=Style.Weight/2;
        var directions=new Vector2[segments];
        for(int k=0;k<segments;k++)directions[k]=Vector2.Normalize(points[(k+1)%n]-points[k]);
        static Vector2 Perp(Vector2 d)=>new(-d.Y,d.X);
        var left=new Vector2[n];var right=new Vector2[n];
        for(int i=0;i<n;i++) {
            bool hasPrev=close||i>0,hasNext=close||i<n-1;
            Vector2 offset;
            if(hasPrev&&hasNext) {
                var n1=Perp(directions[(i-1+segments)%segments]);var n2=Perp(directions[i%segments]);
                var middle=n1+n2;if(middle.LengthSquared()<1e-8f)return false;middle=Vector2.Normalize(middle);
                // Miter longer than 2x the weight, or a round join at a visible corner: use the general path.
                // Round joins on gentle bends (curves, arcs, rounded rectangles) are indistinguishable from miters.
                float denom=Vector2.Dot(middle,n2);if(denom<(Style.Join==1?0.966f:0.5f))return false;
                offset=middle*half/denom;
            } else offset=Perp(directions[hasNext?i:segments-1])*half;
            var point=points[i];
            if(!close&&Style.Cap==2) {
                if(i==0)point-=directions[0]*half;
                if(i==n-1)point+=directions[segments-1]*half;
            }
            left[i]=point+offset;right[i]=point-offset;
        }
        var vertices=new List<ProcessingVertex>(segments*6);
        for(int k=0;k<segments;k++) {
            int j=(k+1)%n;
            Vector3 l0=new(left[k],0),r0=new(right[k],0),l1=new(left[j],0),r1=new(right[j],0);
            vertices.AddRange(new[]{V(l0,Style.Stroke),V(r0,Style.Stroke),V(l1,Style.Stroke),V(l1,Style.Stroke),V(r0,Style.Stroke),V(r1,Style.Stroke)});
        }
        Draw(vertices,lit:false);
        if(!close&&Style.Cap==1){Disk(source[0],half,Style.Stroke);Disk(source[^1],half,Style.Stroke);}
        return true;
    }
    internal void Segment(Vector3 a,Vector3 b) {
        if(!Style.HasStroke||Style.Weight<=0) return;
        Batched(()=>SegmentGeometry(a,b));
    }
    private void SegmentGeometry(Vector3 a,Vector3 b) {
        if(Is3D) {
            var m=Model*View*Projection;var ca=Vector4.Transform(new Vector4(a,1),m);var cb=Vector4.Transform(new Vector4(b,1),m);
            if(ca.W<=0||cb.W<=0)return;
            Vector2 d2=new((cb.X/cb.W-ca.X/ca.W)*Width/2,(cb.Y/cb.W-ca.Y/ca.W)*Height/2);float length=d2.Length();
            if(length<1e-6f){Disk(a,Style.Weight/2,Style.Stroke);return;}
            var n2=new Vector2(-d2.Y,d2.X)/length*Style.Weight/2;
            var ap=StrokeOffset(a,n2.X,n2.Y);var am=StrokeOffset(a,-n2.X,-n2.Y);var bp=StrokeOffset(b,n2.X,n2.Y);var bm=StrokeOffset(b,-n2.X,-n2.Y);
            Draw(new[]{V(ap,Style.Stroke),V(am,Style.Stroke),V(bp,Style.Stroke),V(bp,Style.Stroke),V(am,Style.Stroke),V(bm,Style.Stroke)},lit:false);
            if(Style.Cap==1){Disk(a,Style.Weight/2,Style.Stroke);Disk(b,Style.Weight/2,Style.Stroke);}return;
        }
        Vector3 d=b-a; float len=new Vector2(d.X,d.Y).Length();
        if(len<1e-6f) { Disk(a,Style.Weight/2,Style.Stroke); return; }
        Vector3 n=new(-d.Y/len*Style.Weight/2,d.X/len*Style.Weight/2,0);
        if(Style.Cap==2) { Vector3 e=d/len*Style.Weight/2; a-=e; b+=e; }
        Draw(new[]{V(a+n,Style.Stroke),V(a-n,Style.Stroke),V(b+n,Style.Stroke),V(b+n,Style.Stroke),V(a-n,Style.Stroke),V(b-n,Style.Stroke)},lit:false);
        if(Style.Cap==1) { Disk(a,Style.Weight/2,Style.Stroke); Disk(b,Style.Weight/2,Style.Stroke); }
    }
    internal void Disk(Vector3 center,float radius,Vector4 color) {
        var v=new List<ProcessingVertex>();
        for(int i=0;i<16;i++) { float a=i*MathF.Tau/16,b=(i+1)*MathF.Tau/16;
            Vector3 Offset(float angle)=>Is3D?StrokeOffset(center,MathF.Cos(angle)*radius,MathF.Sin(angle)*radius):center+new Vector3(MathF.Cos(angle)*radius,MathF.Sin(angle)*radius,0);
            v.Add(V(center,color)); v.Add(V(Offset(a),color)); v.Add(V(Offset(b),color)); }
        Draw(v,lit:false);
    }
    private Vector3 StrokeOffset(Vector3 p,float x,float y) {
        var matrix=Model*View*Projection;if(!Matrix4x4.Invert(matrix,out var inverse))return p;
        var clip=Vector4.Transform(new Vector4(p,1),matrix);clip.X+=x*2/Width*clip.W;clip.Y+=y*2/Height*clip.W;
        var world=Vector4.Transform(clip,inverse);return new Vector3(world.X,world.Y,world.Z)/world.W;
    }
    internal void Ellipse(float x,float y,float w,float h,float start=0,float stop=MathF.Tau,int mode=2,bool arc=false) {
        RequireDrawing(); var b=ProcessingGeometry.Bounds(x,y,w,h,Style.EllipseMode);
        float rx=b.w/2,ry=b.h/2,cx=b.x+rx,cy=b.y+ry;
        if(stop<=start) return; stop=Math.Min(stop,start+MathF.Tau);
        int count=Math.Max(2,(int)MathF.Ceiling(Detail*(stop-start)/MathF.Tau));
        var pts=Enumerable.Range(0,count+1).Select(i=>new Vector3(cx+rx*MathF.Cos(start+(stop-start)*i/count),cy+ry*MathF.Sin(start+(stop-start)*i/count),0)).ToList();
        if(!arc||mode==2) {
            if(Style.HasFill) { var v=new List<ProcessingVertex>(); for(int i=0;i<count;i++) v.AddRange(new[]{V(new(cx,cy,0),Style.Fill),V(pts[i],Style.Fill),V(pts[i+1],Style.Fill)}); Draw(v); }
            if(Style.HasStroke) { if(arc) { var outline=new List<Vector3>{new(cx,cy,0)}; outline.AddRange(pts); Polyline(outline,true); } else Polyline(pts); }
        } else {
            if(Style.HasFill) Draw(ProcessingGeometry.Triangulate(pts).Select(i=>V(pts[i],Style.Fill)).ToArray());
            Polyline(pts,mode==1);
        }
    }
    internal void Image(ProcessingImage image,float x,float y,float? w=null,float? h=null) {
        RequireDrawing(); if(Surface==image) throw new ArgumentException("Cannot draw a canvas into itself.");
        var b=ProcessingGeometry.Bounds(x,y,w??image.Canvas?.Width??image.Width,h??image.Canvas?.Height??image.Height,Style.ImageMode);
        float t=image.Flipped?1:0,bt=1-t;
        var a=V(new(b.x,b.y,0),Style.Tint,new(0,t)); var c=V(new(b.x+b.w,b.y+b.h,0),Style.Tint,new(1,bt));
        Draw(new[]{a,V(new(b.x+b.w,b.y,0),Style.Tint,new(1,t)),c,a,c,V(new(b.x,b.y+b.h,0),Style.Tint,new(0,bt))},image:image,lit:false);
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
