using System.Numerics;
using AquariusLang.Object;
using AquaEnvironment=AquariusLang.Object.Environment;

namespace AquariusREPL.Graphics;

internal sealed partial class GraphicsRuntime {
    private Random processingRandom=new();
    private int[] noisePermutation=NoisePermutation(0);
    private int noiseOctaves=4;
    private double noiseFalloff=.5;
    private void RegisterProcessingMath(AquaEnvironment env) {
        foreach(var value in new Dictionary<string,double>{["PI"]=Math.PI,["TWO_PI"]=Math.Tau,["TAU"]=Math.Tau,["HALF_PI"]=Math.PI/2,["QUARTER_PI"]=Math.PI/4})env.Create(value.Key,new DoubleObj(value.Value));
        var unary=new Dictionary<string,Func<double,double>> { ["abs"]=Math.Abs,["ceil"]=Math.Ceiling,["floor"]=Math.Floor,["round"]=x=>Math.Floor(x+.5),["sqrt"]=Math.Sqrt,["sq"]=x=>x*x,["exp"]=Math.Exp,["log"]=Math.Log,
            ["sin"]=Math.Sin,["cos"]=Math.Cos,["tan"]=Math.Tan,["asin"]=Math.Asin,["acos"]=Math.Acos,["atan"]=Math.Atan,["radians"]=x=>x*Math.PI/180,["degrees"]=x=>x*180/Math.PI };
        foreach(var pair in unary) {var f=pair.Value;bool integerResult=pair.Key is "floor" or "ceil" or "round";PBind(env,pair.Key,1,1,a=> {double n=f(Number(a[0]));if(!double.IsFinite(n))throw new ArgumentException("Arguments are outside the function's domain.");return integerResult?new IntegerObj(checked((int)n)):new DoubleObj(n);});}
        PBind(env,"pow",2,2,a=> {double n=Math.Pow(Number(a[0]),Number(a[1]));if(!double.IsFinite(n))throw new ArgumentException("Invalid power.");return new DoubleObj(n);});
        PBind(env,"atan2",2,2,a=>new DoubleObj(Math.Atan2(Number(a[0]),Number(a[1]))));
        foreach(string name in new[]{"min","max"}) {string operation=name;PBind(env,name,1,64,a=> {var values=a.Length==1&&a[0] is ArrayObj array?array.Elements.AsSpan():a;if(values.Length==0)throw new ArgumentException("Expected nonempty numbers.");double result=Number(values[0]);for(int i=1;i<values.Length;i++){double value=Number(values[i]);if(operation=="min"?value<result:value>result)result=value;}return new DoubleObj(result);});}
        PBind(env,"constrain",3,3,a=> {double lo=Number(a[1]),hi=Number(a[2]);if(lo>hi)throw new ArgumentException("Minimum exceeds maximum.");return new DoubleObj(Math.Clamp(Number(a[0]),lo,hi));});
        PBind(env,"lerp",3,3,a=>new DoubleObj(Number(a[0])+(Number(a[1])-Number(a[0]))*Number(a[2])));
        PBind(env,"norm",3,3,a=> {double d=Number(a[2])-Number(a[1]);if(d==0)throw new ArgumentException("Input range is zero.");return new DoubleObj((Number(a[0])-Number(a[1]))/d);});
        PBind(env,"map",5,5,a=> {double d=Number(a[2])-Number(a[1]);if(d==0)throw new ArgumentException("Input range is zero.");return new DoubleObj(Number(a[3])+(Number(a[0])-Number(a[1]))/d*(Number(a[4])-Number(a[3])));});
        PBind(env,"dist",4,6,a=> {if(a.Length is not (4 or 6))throw new ArgumentException("Expected 4 or 6 coordinates.");int d=a.Length/2;double sum=0;for(int i=0;i<d;i++)sum+=Math.Pow(Number(a[i])-Number(a[i+d]),2);return new DoubleObj(Math.Sqrt(sum));});
        PBind(env,"mag",2,3,a=>{double sum=0;for(int i=0;i<a.Length;i++)sum+=Math.Pow(Number(a[i]),2);return new DoubleObj(Math.Sqrt(sum));});
        PAction(env,"randomSeed",1,1,a=>processingRandom=new Random(Int(a[0])));
        PBind(env,"random",1,2,a=> {if(a.Length==1&&a[0] is ArrayObj arr){if(arr.Elements.Length==0)throw new ArgumentException("Cannot select from an empty array.");return arr.Elements[processingRandom.Next(arr.Elements.Length)];}
            double lo=a.Length==2?Number(a[0]):0,hi=Number(a[^1]);if(lo>hi)throw new ArgumentException("Minimum exceeds maximum.");return new DoubleObj(lo+processingRandom.NextDouble()*(hi-lo));});
        PBind(env,"randomGaussian",0,0,_=>new DoubleObj(Math.Sqrt(-2*Math.Log(1-processingRandom.NextDouble()))*Math.Cos(Math.Tau*processingRandom.NextDouble())));
        PAction(env,"noiseSeed",1,1,a=>noisePermutation=NoisePermutation(Int(a[0])));
        PAction(env,"noiseDetail",1,2,a=> {int n=Int(a[0]);if(n<1||n>16)throw new ArgumentException("Noise octaves must be 1..16.");double falloff=a.Length==2?Number(a[1]):noiseFalloff;if(falloff<=0||falloff>1)throw new ArgumentException("Noise falloff must be >0 and <=1.");noiseOctaves=n;noiseFalloff=falloff;});
        PBind(env,"noise",1,3,a=>new DoubleObj(Noise(Number(a[0]),a.Length>1?Number(a[1]):0,a.Length>2?Number(a[2]):0)));
        PBind(env,"bezierPoint",5,5,a=>new DoubleObj(ProcessingGeometry.Bezier(F(a[0]),F(a[1]),F(a[2]),F(a[3]),F(a[4]))));
        PBind(env,"bezierTangent",5,5,a=>new DoubleObj(ProcessingGeometry.BezierTangent(F(a[0]),F(a[1]),F(a[2]),F(a[3]),F(a[4]))));
        PBind(env,"curvePoint",5,5,a=>new DoubleObj(ProcessingGeometry.Curve(F(a[0]),F(a[1]),F(a[2]),F(a[3]),F(a[4]))));
        PBind(env,"curveTangent",5,5,a=> {float t=F(a[4]),e=.0001f;return new DoubleObj((ProcessingGeometry.Curve(F(a[0]),F(a[1]),F(a[2]),F(a[3]),t+e)-ProcessingGeometry.Curve(F(a[0]),F(a[1]),F(a[2]),F(a[3]),t-e))/(2*e));});
        PBind(env,"createVector",2,3,a=>CreateVector(new(F(a[0]),F(a[1]),a.Length==3?F(a[2]):0)));
        PBind(env,"PVector",2,3,a=>CreateVector(new(F(a[0]),F(a[1]),a.Length==3?F(a[2]):0)));
    }
    private ModuleObj CreateVector(Vector3 vector) {
        var env=AquaEnvironment.NewEnvironment();var module=new ModuleObj(env);
        void Update(){env.Create("x",new FloatObj(vector.X));env.Create("y",new FloatObj(vector.Y));env.Create("z",new FloatObj(vector.Z));}
        void Mutate(string name,int min,int max,VectorMutation fn)=>PBind(env,name,min,max,a=>{vector=fn(a);Update();return module;});
        Update();
        Mutate("set",2,3,a=>new(F(a[0]),F(a[1]),a.Length==3?F(a[2]):0));
        Mutate("add",1,1,a=>vector+ReadVector(a[0]));Mutate("sub",1,1,a=>vector-ReadVector(a[0]));Mutate("mult",1,1,a=>vector*F(a[0]));
        Mutate("div",1,1,a=> {float n=F(a[0]);if(n==0)throw new ArgumentException("Division by zero.");return vector/n;});
        Mutate("normalize",0,0,_=>vector.LengthSquared()==0?vector:Vector3.Normalize(vector));
        Mutate("setMag",1,1,a=>vector.LengthSquared()==0?vector:Vector3.Normalize(vector)*F(a[0]));
        Mutate("limit",1,1,a=> {float max=Positive(a[0]);return vector.Length()>max?Vector3.Normalize(vector)*max:vector;});
        Mutate("rotate",1,1,a=>Vector3.Transform(vector,Matrix4x4.CreateRotationZ(F(a[0]))));
        Mutate("lerp",2,2,a=>Vector3.Lerp(vector,ReadVector(a[0]),F(a[1])));
        PBind(env,"copy",0,0,_=>CreateVector(vector));PBind(env,"array",0,0,_=>Numbers(vector.X,vector.Y,vector.Z));
        PBind(env,"mag",0,0,_=>new DoubleObj(vector.Length()));PBind(env,"magSq",0,0,_=>new DoubleObj(vector.LengthSquared()));
        PBind(env,"heading",0,0,_=>new DoubleObj(Math.Atan2(vector.Y,vector.X)));
        PBind(env,"dot",1,1,a=>new DoubleObj(Vector3.Dot(vector,ReadVector(a[0]))));PBind(env,"cross",1,1,a=>CreateVector(Vector3.Cross(vector,ReadVector(a[0]))));
        PBind(env,"dist",1,1,a=>new DoubleObj(Vector3.Distance(vector,ReadVector(a[0]))));
        PBind(env,"angleBetween",1,1,a=> {var other=ReadVector(a[0]);float d=vector.Length()*other.Length();if(d==0)throw new ArgumentException("Angle is undefined for a zero vector.");return new DoubleObj(Math.Acos(Math.Clamp(Vector3.Dot(vector,other)/d,-1,1)));});
        return module;
    }
    private static Vector3 ReadVector(IObject o) {
        if(o is ArrayObj a) {if(a.Elements.Length is not (2 or 3))throw new ArgumentException("Expected two or three vector components.");return new(F(a.Elements[0]),F(a.Elements[1]),a.Elements.Length==3?F(a.Elements[2]):0);}
        if(o is ModuleObj m) {var e=m._Environment;return new(F(e.Get("x",out _)),F(e.Get("y",out _)),F(e.Get("z",out _)));}
        throw new ArgumentException("Expected a PVector or numeric array.");
    }
    private static int[] NoisePermutation(int seed) {var random=new Random(seed);var p=Enumerable.Range(0,256).ToArray();for(int i=255;i>0;i--){int j=random.Next(i+1);(p[i],p[j])=(p[j],p[i]);}return p.Concat(p).ToArray();}
    private double Noise(double x,double y,double z) {
        x=Math.Abs(x)%256;y=Math.Abs(y)%256;z=Math.Abs(z)%256;
        double sum=0,amp=.5,total=0;for(int i=0;i<noiseOctaves;i++){sum+=(Perlin(x,y,z)+1)*.5*amp;total+=amp;amp*=noiseFalloff;x*=2;y*=2;z*=2;}return Math.Clamp(sum/total,0,1);
    }
    private double Perlin(double x,double y,double z) {
        x=Math.Abs(x)%256;y=Math.Abs(y)%256;z=Math.Abs(z)%256;
        int X=(int)Math.Floor(x)&255,Y=(int)Math.Floor(y)&255,Z=(int)Math.Floor(z)&255;x-=Math.Floor(x);y-=Math.Floor(y);z-=Math.Floor(z);
        double Fade(double t)=>t*t*t*(t*(t*6-15)+10);double Lerp(double a,double b,double t)=>a+(b-a)*t;
        double Grad(int h,double a,double b,double c){h&=15;double u=h<8?a:b,v=h<4?b:h is 12 or 14?a:c;return ((h&1)==0?u:-u)+((h&2)==0?v:-v);}
        var p=noisePermutation;int A=p[X]+Y,AA=p[A]+Z,AB=p[A+1]+Z,B=p[X+1]+Y,BA=p[B]+Z,BB=p[B+1]+Z;
        double u=Fade(x),v=Fade(y),w=Fade(z);
        return Lerp(Lerp(Lerp(Grad(p[AA],x,y,z),Grad(p[BA],x-1,y,z),u),Lerp(Grad(p[AB],x,y-1,z),Grad(p[BB],x-1,y-1,z),u),v),
            Lerp(Lerp(Grad(p[AA+1],x,y,z-1),Grad(p[BA+1],x-1,y,z-1),u),Lerp(Grad(p[AB+1],x,y-1,z-1),Grad(p[BB+1],x-1,y-1,z-1),u),v),w);
    }
}
