using AquariusLang.Object;
using AquaEnvironment=AquariusLang.Object.Environment;

namespace AquariusREPL.Graphics;

internal sealed partial class GraphicsRuntime {
    private readonly Dictionary<ModuleObj,WgpuShader> processingShaders=new();
    private void RegisterCanvasShaders(AquaEnvironment env,ProcessingCanvas c) {
        PBind(env,"createShader",2,2,a=>CreateProcessingShader(Text(a[0]),Text(a[1])));
        PBind(env,"loadShader",1,2,a=>CreateProcessingShader(a.Length==2?File.ReadAllText(ResourcePath(Text(a[1]))):WgpuShaders.Vertex,File.ReadAllText(ResourcePath(Text(a[0])))));
        PAction(env,"shader",1,1,a=> {
            RequireSketch();if(a[0] is not ModuleObj module||!processingShaders.TryGetValue(module,out var program)||program.Disposed)throw new ArgumentException("Expected a shader from this runtime.");c.CustomProgram=program;
        });
        PAction(env,"resetShader",0,0,_=>c.CustomProgram=null);
    }
    private ModuleObj CreateProcessingShader(string vertex,string fragment) {
        RequireSketch();var program=new WgpuShader(processingDevice!,vertex,fragment);
        var env=AquaEnvironment.NewEnvironment();var module=new ModuleObj(env);processingShaders.Add(module,program);
        PAction(env,"set",2,5,a=> {
            RequireSketch();if(program.Disposed)throw new ArgumentException("Shader is disposed.");
            var values=a.Length==2&&a[1] is ArrayObj array?array.Elements:a.Skip(1).ToArray();
            program.Uniforms.Set(Text(a[0]),values.Select(F).ToArray());
        });
        PAction(env,"setInt",2,2,a=> {
            RequireSketch();if(program.Disposed)throw new ArgumentException("Shader is disposed.");program.Uniforms.Set(Text(a[0]),new float[1],true,Int(a[1]));
        });
        return module;
    }
}
