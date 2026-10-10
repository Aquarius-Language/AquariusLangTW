using System.Numerics;

namespace AquariusLang.Desktop.Graphics;

// Geometry and color calculations stay independent of the window/driver.
internal static class ProcessingGeometry {
    internal static Vector4 Unpack(uint c) => new((c >> 16 & 255) / 255f, (c >> 8 & 255) / 255f, (c & 255) / 255f, (c >> 24) / 255f);
    internal static uint Pack(Vector4 c) {
        uint Byte(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
        return Byte(c.W) << 24 | Byte(c.X) << 16 | Byte(c.Y) << 8 | Byte(c.Z);
    }
    internal static Vector4 Hsb(float h, float s, float b, float a) {
        h = (h - MathF.Floor(h)) * 6; s = Math.Clamp(s, 0, 1); b = Math.Clamp(b, 0, 1);
        float p = b * (1 - s), q = b * (1 - s * (h % 1)), t = b * (1 - s * (1 - h % 1));
        return (int)h switch { 0 => new(b,t,p,a), 1 => new(q,b,p,a), 2 => new(p,b,t,a),
            3 => new(p,q,b,a), 4 => new(t,p,b,a), _ => new(b,p,q,a) };
    }
    internal static Vector3 RgbToHsb(Vector4 c) {
        float max = Math.Max(c.X, Math.Max(c.Y,c.Z)), min = Math.Min(c.X,Math.Min(c.Y,c.Z)), d = max-min;
        float h = d == 0 ? 0 : max == c.X ? ((c.Y-c.Z)/d + 6) % 6 : max == c.Y ? (c.Z-c.X)/d+2 : (c.X-c.Y)/d+4;
        return new(h/6, max == 0 ? 0 : d/max, max);
    }
    internal static (float x,float y,float w,float h) Bounds(float x,float y,float w,float h,int mode) => mode switch {
        1 => (x,y,w-x,h-y), 2 => (x-w/2,y-h/2,w,h), 3 => (x-w,y-h,w*2,h*2), _ => (x,y,w,h)
    };
    internal static float Bezier(float a,float b,float c,float d,float t) { float s=1-t; return s*s*s*a+3*s*s*t*b+3*s*t*t*c+t*t*t*d; }
    internal static float BezierTangent(float a,float b,float c,float d,float t) { float s=1-t; return 3*s*s*(b-a)+6*s*t*(c-b)+3*t*t*(d-c); }
    internal static float Curve(float a,float b,float c,float d,float t,float tightness=0) {
        float s=(1-tightness)/2; return Bezier(b,b+s*(c-a)/3,c-s*(d-b)/3,c,t);
    }
    // Ear clipping handles concave simple polygons, in either winding direction.
    internal static List<int> Triangulate(IReadOnlyList<Vector3> vertices) {
        var result=new List<int>(); if(vertices.Count<3) return result;
        // Project a planar 3D polygon onto its dominant plane.
        Vector3 normal=Vector3.Zero;
        for(int i=0;i<vertices.Count;i++){var a=vertices[i];var b=vertices[(i+1)%vertices.Count];normal+=new Vector3((a.Y-b.Y)*(a.Z+b.Z),(a.Z-b.Z)*(a.X+b.X),(a.X-b.X)*(a.Y+b.Y));}
        if(Math.Abs(normal.X)>Math.Abs(normal.Z)&&Math.Abs(normal.X)>=Math.Abs(normal.Y))vertices=vertices.Select(p=>new Vector3(p.Y,p.Z,0)).ToArray();
        else if(Math.Abs(normal.Y)>Math.Abs(normal.Z))vertices=vertices.Select(p=>new Vector3(p.X,p.Z,0)).ToArray();
        var ids=Enumerable.Range(0,vertices.Count).ToList();
        float Cross(Vector3 a,Vector3 b,Vector3 c) => (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        bool removed=true;while(removed&&ids.Count>3){removed=false;for(int i=0;i<ids.Count;i++) {var a=vertices[ids[(i+ids.Count-1)%ids.Count]];var b=vertices[ids[i]];var c=vertices[ids[(i+1)%ids.Count]];
            if(Vector3.DistanceSquared(a,b)<1e-12f||Math.Abs(Cross(a,b,c))<1e-6f){ids.RemoveAt(i);removed=true;break;}}}
        float area=0; for(int i=0;i<ids.Count;i++) { var a=vertices[ids[i]]; var b=vertices[ids[(i+1)%ids.Count]]; area+=a.X*b.Y-b.X*a.Y; }
        if(Math.Abs(area)<1e-6f)throw new ArgumentException("Polygon has zero area.");
        float sign=area>=0?1:-1;
        while(ids.Count>3) {
            bool found=false;
            for(int i=0;i<ids.Count;i++) {
                int a=ids[(i+ids.Count-1)%ids.Count],b=ids[i],c=ids[(i+1)%ids.Count];
                if(sign*Cross(vertices[a],vertices[b],vertices[c])<=1e-6f) continue;
                bool inside=ids.Any(p=>p!=a&&p!=b&&p!=c && sign*Cross(vertices[a],vertices[b],vertices[p])>=-1e-6f
                    && sign*Cross(vertices[b],vertices[c],vertices[p])>=-1e-6f && sign*Cross(vertices[c],vertices[a],vertices[p])>=-1e-6f);
                if(inside) continue;
                result.AddRange(new[]{a,b,c}); ids.RemoveAt(i); found=true; break;
            }
            if(!found) throw new ArgumentException("Polygon boundary must be simple and have nonzero area; use beginContour() for holes.");
        }
        if(ids.Count==3) result.AddRange(ids); return result;
    }
}

internal sealed class ProcessingStyle {
    internal Vector4 Fill=Vector4.One, Stroke=new(0,0,0,1), Tint=Vector4.One;
    internal bool HasFill=true, HasStroke=true, Lit;
    internal float Weight=1, TextSize=12, Leading=14, Shininess=16;
    internal int RectMode, EllipseMode=2, ImageMode, ColorMode, TextAlign, TextVertical=103, Cap=1, Join;
    internal Vector4 ColorMax=new(255,255,255,255);
    internal Vector3 Ambient=new(.2f), MaterialAmbient=Vector3.One, LightColor=Vector3.One, LightDirection=new(0,0,-1), Specular=new(.25f), Emissive, Falloff=new(1,0,0), LightSpecular;
    internal List<ProcessingLight> Lights=new();
    internal string Font="sans-serif";
    internal ProcessingStyle Copy() {var copy=(ProcessingStyle)MemberwiseClone();copy.Lights=new(Lights);return copy;}
}

internal readonly record struct ProcessingLight(int Kind,Vector3 Color,Vector3 Position,Vector3 Direction,Vector3 Falloff,Vector3 Specular,float Cutoff,float Concentration);

internal readonly record struct ProcessingVertex(Vector3 Position, Vector3 Normal, Vector2 UV, Vector4 Color);
