using System.Numerics;
using System.Runtime.InteropServices;
using AquariusLang.Object;

namespace AquariusREPL.Graphics;

internal sealed partial class ProcessingCanvas : IDisposable {
    private readonly Func<string,IObject[],IObject> call;
    internal readonly GraphicsRuntime Owner;
    internal int Width, Height, PixelWidth, PixelHeight, Framebuffer;
    internal ProcessingImage? Surface;
    internal ProcessingStyle Style=new();
    internal Matrix4x4 Model=Matrix4x4.Identity, View=Matrix4x4.Identity, Projection;
    internal readonly Stack<Matrix4x4> Matrices=new();
    internal readonly Stack<ProcessingStyle> Styles=new();
    private int program, vao, vbo, depth;
    internal int CustomProgram;
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
    internal Vector4? Clip;
    internal ModuleObj? Module;

    internal ProcessingCanvas(GraphicsRuntime owner,Func<string,IObject[],IObject> call,int w,int h,bool threeD) {
        Owner=owner; this.call=call; Width=PixelWidth=w; Height=PixelHeight=h; Is3D=threeD; DefaultCamera();
    }
    internal IObject GL(string name,params object[] args) => call(name,args.Select(Convert).ToArray());
    private static IObject Convert(object o) => o switch {
        IObject value=>value, int n=>new IntegerObj(n), uint n=>new DoubleObj(n), float n=>new FloatObj(n),
        double n=>new DoubleObj(n), bool b=>new BooleanObj(b), string s=>new StringObj(s),
        Matrix4x4 m=>GraphicsRuntime.Numbers(m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44),
        _=>throw new ArgumentException("Unsupported renderer argument.")
    };
    private int Gen(string name) { var a=GraphicsRuntime.Numbers(0); GL(name,1,a); return GraphicsRuntime.Int(a.Elements[0]); }
    internal void Initialize(bool offscreen=false) {
        try {
            program=GraphicsRuntime.Int(GL("CreateProgram",VertexShader,FragmentShader));
            vao=Gen("glGenVertexArrays"); vbo=Gen("glGenBuffers");
            GL("glBindVertexArray",vao); GL("glBindBuffer",0x8892,vbo);
            int[] sizes={3,3,2,4}; int offset=0;
            for(int i=0;i<4;i++) { GL("glVertexAttribPointer",i,sizes[i],0x1406,false,48,offset); GL("glEnableVertexAttribArray",i); offset+=sizes[i]*4; }
            if(offscreen) {
                Surface=ProcessingImage.Create(Owner,Width,Height); Surface.Upload(this);
                Framebuffer=Gen("glGenFramebuffers"); depth=Gen("glGenRenderbuffers");
                GL("glBindFramebuffer",0x8D40,Framebuffer); GL("glFramebufferTexture2D",0x8D40,0x8CE0,0x0DE1,Surface.Texture,0);
                GL("glBindRenderbuffer",0x8D41,depth); GL("glRenderbufferStorage",0x8D41,0x88F0,Width,Height);
                GL("glFramebufferRenderbuffer",0x8D40,0x821A,0x8D41,depth);
                if(GraphicsRuntime.Int(GL("glCheckFramebufferStatus",0x8D40))!=0x8CD5) throw new InvalidOperationException("Offscreen framebuffer is incomplete.");
                Surface.Flipped=true;
                Surface.Premultiplied=true;
            }
            Bind(); Background(new Vector4(.8f,.8f,.8f,1));
        } catch { Dispose(); throw; }
    }
    internal void RequireDrawing() {
        if(Disposed) throw new InvalidOperationException("Canvas has been disposed.");
        if(!Drawing) throw new InvalidOperationException("Call size() for the main canvas, or beginDraw() for PGraphics first.");
        if(ShapeMode!=-1) throw new InvalidOperationException("Finish beginShape() with endShape() before drawing another primitive.");
    }
    internal void Bind() {
        GL("glBindFramebuffer",0x8D40,Framebuffer); GL("glViewport",0,0,PixelWidth,PixelHeight);
        GL("glEnable",0x0BE2); GL("glBlendFuncSeparate",BlendMode switch{2=>0x0306,3=>1,4=>1,_=>0x0302},BlendMode switch{1=>1,2=>0,3=>0x0301,4=>0,_=>0x0303},1,BlendMode is 1?1:BlendMode is 4?0:0x0303); GL(Is3D?"glEnable":"glDisable",0x0B71);
        GL("glDisable",0x0B44);
        GL(Smooth?"glEnable":"glDisable",0x809D);
        GL(Clip==null?"glDisable":"glEnable",0x0C11);
        if(Clip is Vector4 clip) {float sx=(float)PixelWidth/Width,sy=(float)PixelHeight/Height;GL("glScissor",(int)(clip.X*sx),(int)((Height-clip.Y-clip.W)*sy),(int)(clip.Z*sx),(int)(clip.W*sy));}
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
    internal void Background(Vector4 c) { Bind(); GL("glClearColor",c.X*c.W,c.Y*c.W,c.Z*c.W,c.W); GL("glClear",0x4000|0x0100|0x0400); }
    private void Uniform(string name,params object[] values) {
        int loc=GraphicsRuntime.Int(GL("glGetUniformLocation",CustomProgram==0?program:CustomProgram,name));
        var a=new List<object>{loc}; a.AddRange(values);
        GL(values.Length==1 && values[0] is Matrix4x4 ? "glUniformMatrix4fv" : values.Length switch { 1=>"glUniform1f",3=>"glUniform3f",_=>"glUniform4f" },
            values.Length==1 && values[0] is Matrix4x4 ? new object[]{loc,1,false,values[0]} : a.ToArray());
    }
    internal void Draw(IReadOnlyList<ProcessingVertex> vertices,int mode=4,ProcessingImage? image=null,bool lit=true) {
        if(vertices.Count==0) return;
        Bind(); GL("glUseProgram",CustomProgram==0?program:CustomProgram); GL("glBindVertexArray",vao); GL("glBindBuffer",0x8892,vbo);
        var floats=new float[vertices.Count*12]; int i=0;
        foreach(var v in vertices) {
            foreach(float f in new[]{v.Position.X,v.Position.Y,v.Position.Z,v.Normal.X,v.Normal.Y,v.Normal.Z,v.UV.X,v.UV.Y,v.Color.X,v.Color.Y,v.Color.Z,v.Color.W}) floats[i++]=f;
        }
        using var data=new DataObject(floats.Length*4,Owner); Marshal.Copy(floats,0,data.Handle,floats.Length);
        GL("glBufferData",0x8892,data.Size,data,0x88E0);
        Uniform("model",Model); Uniform("view",View); Uniform("projection",Projection);
        Uniform("lit",Style.Lit && lit ? 1f:0f); Uniform("ambient",Style.Ambient.X,Style.Ambient.Y,Style.Ambient.Z);
        Uniform("lightColor",Style.LightColor.X,Style.LightColor.Y,Style.LightColor.Z);
        Uniform("lightDirection",Style.LightDirection.X,Style.LightDirection.Y,Style.LightDirection.Z);
        Uniform("specular",Style.Specular.X,Style.Specular.Y,Style.Specular.Z); Uniform("emissive",Style.Emissive.X,Style.Emissive.Y,Style.Emissive.Z);
        Uniform("shininess",Style.Shininess); Uniform("textured",image==null?0f:1f);
        Uniform("imagePremultiplied",image?.Premultiplied==true?1f:0f);Uniform("replaceMode",BlendMode==4?1f:0f);
        Uniform("materialAmbient",Style.MaterialAmbient.X,Style.MaterialAmbient.Y,Style.MaterialAmbient.Z);
        GL("glUniform1i",GraphicsRuntime.Int(GL("glGetUniformLocation",CustomProgram==0?program:CustomProgram,"lightCount")),Style.Lights.Count);
        for(int light=0;light<Style.Lights.Count;light++) {
            var l=Style.Lights[light];string prefix=$"lights[{light}].";
            Uniform(prefix+"kind",(float)l.Kind);Uniform(prefix+"color",l.Color.X,l.Color.Y,l.Color.Z);
            var pos=Vector3.Transform(l.Position,View);var dir=Vector3.TransformNormal(l.Direction,View);
            Uniform(prefix+"position",pos.X,pos.Y,pos.Z);Uniform(prefix+"direction",dir.X,dir.Y,dir.Z);
            Uniform(prefix+"falloff",l.Falloff.X,l.Falloff.Y,l.Falloff.Z);Uniform(prefix+"specular",l.Specular.X,l.Specular.Y,l.Specular.Z);
            Uniform(prefix+"cutoff",l.Cutoff);Uniform(prefix+"concentration",l.Concentration);
        }
        if(image!=null) { image.Upload(this); GL("glActiveTexture",0x84C0); GL("glBindTexture",0x0DE1,image.Texture); GL("glUniform1i",GraphicsRuntime.Int(GL("glGetUniformLocation",CustomProgram==0?program:CustomProgram,"surface")),0); }
        GL("glDrawArrays",mode,0,vertices.Count);
    }
    internal ProcessingVertex V(Vector3 p,Vector4 color,Vector2 uv=default,Vector3? normal=null) => new(p,normal??Normal,uv,color);
    internal void Polygon(IReadOnlyList<Vector3> points,bool close=true,bool fill=true) {
        RequireDrawing();
        if(Style.HasFill&&fill&&points.Count>=3) Draw(ProcessingGeometry.Triangulate(points).Select(i=>V(points[i],Style.Fill)).ToArray());
        if(Style.HasStroke) Polyline(points,close);
    }
    internal void Polyline(IReadOnlyList<Vector3> points,bool close=false) {
        if(!Style.HasStroke||points.Count<2) return;
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
    internal void Segment(Vector3 a,Vector3 b) {
        if(!Style.HasStroke||Style.Weight<=0) return;
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
        var b=ProcessingGeometry.Bounds(x,y,w??image.Width,h??image.Height,Style.ImageMode);
        float t=image.Flipped?1:0,bt=1-t;
        var a=V(new(b.x,b.y,0),Style.Tint,new(0,t)); var c=V(new(b.x+b.w,b.y+b.h,0),Style.Tint,new(1,bt));
        Draw(new[]{a,V(new(b.x+b.w,b.y,0),Style.Tint,new(1,t)),c,a,c,V(new(b.x,b.y+b.h,0),Style.Tint,new(0,bt))},image:image,lit:false);
    }
    internal byte[] ReadPixels() {
        Bind(); using var data=new DataObject(checked(PixelWidth*PixelHeight*4),Owner);
        GL("glPixelStorei",0x0D05,1); GL("glReadPixels",0,0,PixelWidth,PixelHeight,0x1908,0x1401,data);
        var bytes=new byte[data.Size]; Marshal.Copy(data.Handle,bytes,0,bytes.Length);
        var top=new byte[bytes.Length]; for(int y=0;y<PixelHeight;y++) System.Array.Copy(bytes,y*PixelWidth*4,top,(PixelHeight-y-1)*PixelWidth*4,PixelWidth*4);
        for(int i=0;i<top.Length;i+=4)if(top[i+3]>0&&top[i+3]<255)for(int c=0;c<3;c++)top[i+c]=(byte)Math.Min(255,(top[i+c]*255+top[i+3]/2)/top[i+3]);
        return top;
    }
    public void Dispose() {
        if(Disposed) return; Disposed=true;
        if(program!=0) GL("glDeleteProgram",program);
        if(vbo!=0) GL("glDeleteBuffers",1,GraphicsRuntime.Numbers(vbo));
        if(vao!=0) GL("glDeleteVertexArrays",1,GraphicsRuntime.Numbers(vao));
        if(depth!=0) GL("glDeleteRenderbuffers",1,GraphicsRuntime.Numbers(depth));
        if(Framebuffer!=0) GL("glDeleteFramebuffers",1,GraphicsRuntime.Numbers(Framebuffer));
        Surface?.Dispose(this);
    }
    internal const string VertexShader=@"#version 330 core
layout(location=0) in vec3 position;
layout(location=1) in vec3 normal;
layout(location=2) in vec2 texcoord;
layout(location=3) in vec4 color;
uniform mat4 model, view, projection;
out vec4 vertexColor; out vec2 uv; out vec3 N; out vec3 eyePosition;
void main(){ vec4 eye=view*model*vec4(position,1); eyePosition=eye.xyz;
N=mat3(transpose(inverse(view*model)))*normal; uv=texcoord; vertexColor=color;
gl_Position=projection*eye; }";
    private const string FragmentShader=@"#version 330 core
in vec4 vertexColor; in vec2 uv; in vec3 N; in vec3 eyePosition;
uniform float textured, lit, shininess, imagePremultiplied, replaceMode;
uniform vec3 ambient, lightColor, lightDirection, specular, emissive, materialAmbient;
struct Light {float kind;vec3 color,position,direction,falloff,specular;float cutoff,concentration;};
uniform Light lights[8];uniform int lightCount;
uniform sampler2D surface;
out vec4 fragColor;
void main(){vec4 c=vertexColor; if(textured>0.5){vec4 tex=texture(surface,uv);if(imagePremultiplied>0.5&&tex.a>0)tex.rgb/=tex.a;c*=tex;}
if(lit>0.5){vec3 n=normalize(N);vec3 l=normalize(-lightDirection);float d=max(dot(n,l),0);
vec3 v=normalize(-eyePosition);float s=d>0?pow(max(dot(n,normalize(l+v)),0),shininess):0;
vec3 diffuseLight=vec3(0);vec3 specularLight=vec3(0);
for(int i=0;i<lightCount;i++){Light light=lights[i];vec3 delta=light.position-eyePosition;
vec3 L=light.kind<0.5?normalize(-light.direction):normalize(delta);float distanceToLight=length(delta);
float attenuation=light.kind<0.5?1.0:1.0/max(dot(light.falloff,vec3(1,distanceToLight,distanceToLight*distanceToLight)),0.0001);
if(light.kind>1.5){float spot=dot(normalize(light.direction),-L);attenuation*=spot>=light.cutoff?pow(max(spot,0),light.concentration):0;}
float lambert=max(dot(n,L),0);diffuseLight+=light.color*lambert*attenuation;
float highlight=lambert>0?pow(max(dot(n,normalize(L+v)),0),shininess):0;specularLight+=light.specular*highlight*attenuation;}
c.rgb=c.rgb*(ambient*materialAmbient+diffuseLight)+specular*specularLight+emissive;}
if(replaceMode>0.5)c.rgb*=c.a;fragColor=c; }";
}

internal sealed class ProcessingImage {
    internal readonly GraphicsRuntime Owner;
    internal int Width,Height;
    internal byte[] Bytes;
    internal int Texture;
    internal bool Dirty=true,Flipped,Disposed,Premultiplied;
    internal ModuleObj Module;
    private ProcessingImage(GraphicsRuntime owner,int w,int h) {
        if(w<=0||h<=0||w>8192||h>8192||checked((long)w*h)>16*1024*1024) throw new ArgumentException("Image dimensions must be 1..8192 with at most 16 million pixels.");
        Owner=owner; Width=w; Height=h; Bytes=new byte[w*h*4]; Module=new ModuleObj(AquariusLang.Object.Environment.NewEnvironment());
        Module._Environment.Create("width",new IntegerObj(w)); Module._Environment.Create("height",new IntegerObj(h));
    }
    internal static ProcessingImage Create(GraphicsRuntime owner,int w,int h) => new(owner,w,h);
    internal void Upload(ProcessingCanvas canvas) {
        if(Disposed) throw new ArgumentException("Image has been disposed.");
        if(Texture==0) { var ids=GraphicsRuntime.Numbers(0); canvas.GL("glGenTextures",1,ids); Texture=GraphicsRuntime.Int(ids.Elements[0]); }
        if(!Dirty) return;
        using var data=new DataObject(Bytes.Length,Owner); Marshal.Copy(Bytes,0,data.Handle,Bytes.Length);
        canvas.GL("glBindTexture",0x0DE1,Texture); canvas.GL("glPixelStorei",0x0CF5,1);
        canvas.GL("glTexImage2D",0x0DE1,0,0x8058,Width,Height,0,0x1908,0x1401,data);
        canvas.GL("glTexParameteri",0x0DE1,0x2801,0x2601); canvas.GL("glTexParameteri",0x0DE1,0x2800,0x2601);
        canvas.GL("glTexParameteri",0x0DE1,0x2802,0x812F); canvas.GL("glTexParameteri",0x0DE1,0x2803,0x812F); Dirty=false;
    }
    internal void Dispose(ProcessingCanvas canvas) { if(Disposed)return; Disposed=true; if(Texture!=0)canvas.GL("glDeleteTextures",1,GraphicsRuntime.Numbers(Texture)); }
}
