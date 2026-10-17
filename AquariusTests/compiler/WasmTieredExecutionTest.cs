using AquariusLang.Wasm;

namespace AquariusTests.Wasm;

/// <summary>Differential coverage for promoted frame-slot scopes, register-resident operands and
/// slots, the i32 integer representation, direct callee execution and the typed scalar tier with
/// deoptimization. Each program must produce the generic emitter's result, also when tiny quanta
/// force yields (register slots written back and reloaded) at every checkpoint.</summary>
public class WasmTieredExecutionTest
{
    [Theory]
    // Promoted scopes: conditional declarations, per-iteration resets, shadowing and redeclaration.
    [InlineData("變數 y=5;變數 f=函式(n){如果(n>0){變數 y=1;} y;};[f(0),f(1)];")]
    [InlineData("變數 x=100;變數 r=陣列.建立(3,0);迴圈(變數 i=0;i<3;i++){如果(i>0){變數 x=i;} r[i]=x;}r;")]
    [InlineData("變數 x=7;變數 out=陣列.建立(2,0);迴圈(變數 i=0;i<2;i++){out[i]=x;變數 x=i;};[out,x];")]
    [InlineData("變數 f=函式(){變數 a=1;變數 a=a+1;a;};f();")]
    [InlineData("變數 f=函式(a){變數 a=a*2;a;};f(21);")]
    [InlineData("變數 g=0;變數 f=函式(){迴圈(變數 i=0;i<5;i++){g+=i;}g;};[f(),g];")]
    [InlineData("變數 s=0;迴圈(變數 i=0;i<10;i++){迴圈(變數 j=0;j<10;j++){如果(j>i){中斷;}s+=j;}如果(i==5){中斷;}}s;")]
    [InlineData("變數 f=函式(){變數 t=0;迴圈(變數 i=0;i<4;i++){變數 k=i;迴圈(變數 j=0;j<k;j++){變數 k=j;t+=k;}t+=k;}t;};f();")]
    // Closures keep dynamic scopes around promoted inner loops.
    [InlineData("變數 fs=陣列.建立(3,0);迴圈(變數 i=0;i<3;i++){變數 t=0;迴圈(變數 j=0;j<=i;j++){t+=j;}fs[i]=函式(){t*10+i;};};[fs[0](),fs[1](),fs[2]()];")]
    [InlineData("變數 f=函式(){變數 a=陣列.建立(2,0);迴圈(變數 i=0;i<2;i++){a[i]=函式(){i;};};[a[0](),a[1]()];};f();")]
    [InlineData("變數 mk=函式(n){變數 c=0;函式(){c+=n;c;};};變數 a=mk(2);a();a();a();")]
    // Errors keep their messages.
    [InlineData("變數 f=函式(){z++;};f();")]
    [InlineData("變數 f=函式(){迴圈(變數 i=0;i;i++){}};f();")]
    [InlineData("變數 f=函式(){變數 z=0;5%z;};f();")]
    [InlineData("變數 f=函式(x){變數 t=0;迴圈(變數 i=0;i<3;i++){t+=x;}t;};f(\"a\");")]
    [InlineData("變數 f=函式(){變數 s=\"\";迴圈(變數 i=0;i<3;i++){s+=\"繁\";}s;};f();")]
    // Integer representation: overflow, division, -0, mixed types and compound type preservation.
    [InlineData("變數 f=函式(){變數 a=2147483647;變數 m=0-2147483647-1;變數 e=0*(0-5);[a+1,m/(0-1),7/0,e,1.0d/e,m*m,0-m,a*a,(0-7)/2];};f();")]
    [InlineData("變數 f=函式(){變數 x=2147483640;迴圈(變數 i=0;i<20;i++){x++;}變數 y=0-2147483647-1;迴圈(變數 j=0;j<3;j++){y-=1;};[x,y];};f();")]
    [InlineData("變數 x=2147483640;迴圈(變數 i=0;i<20;i++){x++;}x;")]
    [InlineData("變數 f=函式(){變數 x=1;x+=0.5d;變數 y=1.5f;y*=3;變數 z=10;z/=4.0d;變數 w=3;w*=2.6d;[x,y,z,w];};f();")]
    [InlineData("變數 f=函式(){[7%3,(0-7)%3,7%(0-3),(0-2147483647-1)%(0-1),7.5d%2,5.5f%2];};f();")]
    [InlineData("變數 f=函式(){變數 a=1;變數 b=1.0d;變數 c=1.0f;[a==b,a<b,a<=b,真==真,a==真,c==a,1.1f==1.1d];};f();")]
    [InlineData("變數 f=函式(){變數 n=0.0d/0.0d;[n==n,n!=n,n<1,n>=1];};f();")]
    [InlineData("變數 f=函式(){變數 t=0.0f;迴圈(變數 i=0;i<10;i++){t+=0.1f;}變數 d=0.0d;迴圈(變數 i=0;i<10;i++){d+=0.1d;};[t,d];};f();")]
    [InlineData("變數 f=函式(){變數 i=0;變數 c=0;迴圈(變數 k=0;k<5;k++){如果(k*2==4||!(k<3)){c+=1;}};[c,!真,!假,-(0-2147483647-1)];};f();")]
    // Arrays: in-range, out-of-range, negative and aliasing writes.
    [InlineData("變數 f=函式(){變數 a=[1,2,3];[a[0-1],a[3],a[2],a[0]];};f();")]
    [InlineData("變數 f=函式(){變數 a=陣列.建立(3,0);變數 b=a;迴圈(變數 i=0;i<3;i++){a[i]=i*1.5d;}變數 same=a==b;[a,b,same];};f();")]
    [InlineData("變數 f=函式(){變數 a=陣列.建立(3,0);a[3]=1;};f();")]
    [InlineData("變數 f=函式(){變數 a=陣列.建立(4,1);變數 s=0;迴圈(變數 i=0;i<4;i++){a[i]=a[i]+i;s+=a[i];};[s,a];};f();")]
    // Calls: direct callee execution, nesting beyond the native bound and builtin calls inline.
    [InlineData("變數 g=函式(n){n*2;};變數 f=函式(){變數 t=0;迴圈(變數 i=0;i<200;i++){t+=g(i);}t;};f();")]
    [InlineData("變數 h=函式(n,s){如果(n==0){回傳 s;}回傳 h(n-1,s);};h(5000,\"ok\");")]
    [InlineData("變數 h=函式(n,s){如果(n==0){回傳 0;}回傳 h(n-1,s)+1;};h(700,\"x\");")]
    [InlineData("變數 build=函式(d){如果(d==0){回傳 [0,0];}回傳 [build(d-1),build(d-1)];};變數 check=函式(t,d){如果(d==0){回傳 1;}回傳 1+check(t[0],d-1)+check(t[1],d-1);};check(build(8),8);")]
    // Typed scalar tier: integer, double, float and boolean results; deoptimization beyond its bounds.
    [InlineData("變數 f=函式(n){如果(n==0){回傳 0;}回傳 f(n-1)+1;};f(3000);")]
    [InlineData("變數 ack=函式(m,n){如果(m==0){回傳 n+1;}如果(n==0){回傳 ack(m-1,1);}回傳 ack(m-1,ack(m,n-1));};[ack(2,300),ack(3,5)];")]
    [InlineData("變數 h=函式(i,j){變數 s=i+j;1.0d/(s*(s+1)/2+i+1);};[h(1,2),h(0,0),h(40000,40000)];")]
    [InlineData("變數 p=函式(n){n>3;};變數 q=函式(n){1.5f*n;};變數 r=函式(n){如果(n>0){回傳 1;}2.5d;};[p(1),p(5),q(3),r(1),r(0)];")]
    [InlineData("變數 d=函式(n){如果(n==0){回傳 0.5d;}回傳 d(n-1)*2.0d;};[d(10),d(2000)];")]
    [InlineData("變數 f=函式(n){n+1;};變數 s=0;迴圈(變數 i=0;i<200;i++){s+=f(1.5d);};[s,f(2),f(2147483647)];")]
    [InlineData("變數 fib=函式(n){如果(n<2){回傳 n;}回傳 fib(n-1)+fib(n-2);};fib(20);")]
    [InlineData("變數 c=函式(n){變數 k=n;如果(k>2){k=k*3;}否則{k=k+1;}k;};[c(1),c(5)];")]
    // Cached binding addresses must survive branches but be discarded after scope changes and calls.
    [InlineData("變數 x=1;變數 read=函式(){x;};變數 s=0;迴圈(變數 i=0;i<20;i++){s+=x;如果(i==10){變數 x=7;}s+=read();};[s,x];")]
    [InlineData("變數 x=1;變數 edit=函式(){x+=2;};變數 s=0;迴圈(變數 i=0;i<20;i++){s+=x;edit();s+=x;};[s,x];")]
    [InlineData("變數 x=10;變數 fs=陣列.建立(4,0);變數 s=0;迴圈(變數 i=0;i<4;i++){s+=x;變數 x=i;fs[i]=函式(){x;};s+=x;};[s,x,fs[0](),fs[3]()];")]
    [InlineData("變數 f=函式(){變數 x=42;回傳 x;x++;};f();")]
    public void TieredExecutionMatchesTheGenericEmitter(string source)
    {
        string Run(bool optimize, int quantum) {
            using var runtime = new WasmRuntime(null, null, quantum);
            var result = runtime.Execute(new WasmCompiler(optimize).Compile(source));
            return result.GetType().Name + ":" + result.Inspect();
        }
        string expected = Run(false, WasmAbi.DesktopQuantum);
        Assert.Equal(expected, Run(true, WasmAbi.DesktopQuantum));
        foreach (int quantum in new[] { 1, 3, 17 }) {
            Assert.Equal(expected, Run(false, quantum));
            Assert.Equal(expected, Run(true, quantum));
        }
    }

    [Fact]
    public void ClosureFreeScopesBecomeFrameSlots()
    {
        var program = new WasmCompiler().Compile("變數 f=函式(alpha,beta){變數 t=0;迴圈(變數 i=0;i<alpha;i++){t+=beta;}t;};變數 g=函式(gamma){函式(){gamma;};};f(2,3);");
        Assert.Equal(40, program.Metadata.FunctionLayout);
        var f = program.Metadata.Functions.Single(x => x.Parameters!.SequenceEqual(new[] { "alpha", "beta" }));
        Assert.True(f.PromotedScope); Assert.True(f.Locals >= 4);
        var g = program.Metadata.Functions.Single(x => x.Parameters!.SequenceEqual(new[] { "gamma" }));
        Assert.False(g.PromotedScope);
        // Unoptimized compilation keeps every scope dynamic.
        Assert.All(new WasmCompiler(false).Compile("變數 f=函式(a){a;};f(1);").Metadata.Functions, x => Assert.Equal(0, x.Locals));
    }

    [Fact]
    public void BindingCacheObservesReallocatedScopeBuffers()
    {
        string source = "變數 x=1;變數 f=函式(){x;};變數 s=x;" +
            string.Concat(Enumerable.Range(0, 160).Select(i => $"變數 padding{i}={i};")) +
            "迴圈(變數 i=0;i<50;i++){s+=x;};[s,f()];";
        using var runtime = new WasmRuntime();
        Assert.Equal("[51, 1]", runtime.Execute(new WasmCompiler().Compile(source)).Inspect());
    }
}
