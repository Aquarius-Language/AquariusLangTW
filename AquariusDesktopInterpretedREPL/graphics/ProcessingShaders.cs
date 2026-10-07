using AquariusLang.Object;
using AquaEnvironment=AquariusLang.Object.Environment;

namespace AquariusREPL.Graphics;

internal sealed partial class GraphicsRuntime {
    private readonly Dictionary<ModuleObj,int> processingShaders=new();
    private void RegisterCanvasShaders(AquaEnvironment env,ProcessingCanvas c) {
        PBind(env,"createShader",2,2,a=>CreateProcessingShader(Text(a[0]),Text(a[1])));
        PBind(env,"loadShader",1,2,a=>CreateProcessingShader(a.Length==2?File.ReadAllText(Text(a[1])):ProcessingCanvas.VertexShader,File.ReadAllText(Text(a[0]))));
        PAction(env,"shader",1,1,a=> {RequireSketch();if(a[0] is not ModuleObj module||!processingShaders.TryGetValue(module,out int program))throw new ArgumentException("Expected a shader from this runtime.");c.CustomProgram=program;});
        PAction(env,"resetShader",0,0,_=>c.CustomProgram=0);
    }
    private ModuleObj CreateProcessingShader(string vertex,string fragment) {
        RequireSketch();int program=Int(CallGl("CreateProgram",new StringObj(vertex),new StringObj(fragment)));
        var env=AquaEnvironment.NewEnvironment();var module=new ModuleObj(env);processingShaders.Add(module,program);
        PAction(env,"set",2,5,a=> {
            RequireSketch();if(!processingShaders.ContainsKey(module))throw new ArgumentException("Shader is disposed.");
            CallGl("glUseProgram",N(program));var loc=CallGl("glGetUniformLocation",N(program),new StringObj(Text(a[0])));
            var values=a.Length==2&&a[1] is ArrayObj array?array.Elements:a.Skip(1).ToArray();
            if(values.Length==16)CallGl("glUniformMatrix4fv",loc,N(1),new BooleanObj(false),new ArrayObj(values));
            else if(values.Length is >=1 and <=4) {
                if(values.Length==1&&values[0] is BooleanObj b)CallGl("glUniform1i",loc,N(b.Value?1:0));
                else {var args=new List<IObject>{loc};args.AddRange(values);CallGl($"glUniform{values.Length}f",args.ToArray());}
            } else throw new ArgumentException("Uniform needs 1..4 values or a 16-element matrix.");
        });
        PAction(env,"setInt",2,2,a=> {RequireSketch();if(!processingShaders.ContainsKey(module))throw new ArgumentException("Shader is disposed.");CallGl("glUseProgram",N(program));CallGl("glUniform1i",CallGl("glGetUniformLocation",N(program),new StringObj(Text(a[0]))),N(Int(a[1])));});
        return module;
    }
}
