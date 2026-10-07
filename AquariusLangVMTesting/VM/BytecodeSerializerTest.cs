using System.Text;
using AquariusLang.Object;
using AquariusLang.runtime;
using AquariusLang.VM;
using AquaEnvironment = AquariusLang.Object.Environment;

namespace AquariusLangVMTesting.VM;

public class BytecodeSerializerTest {
    private static Bytecode RoundTrip(string source) {
        using var stream = new MemoryStream();
        BytecodeSerializer.Write(new VmCompiler().Compile(source), stream);
        stream.Position = 0;
        return BytecodeSerializer.Read(stream);
    }

    [Theory]
    [InlineData("42;", "INTEGER", "42")]
    [InlineData("-42;", "INTEGER", "-42")]
    [InlineData("20.38f;", "FLOAT", "20.38")]
    [InlineData("1.5d;", "DOUBLE", "1.5")]
    [InlineData("真;", "BOOLEAN", "真")]
    [InlineData("假;", "BOOLEAN", "假")]
    [InlineData("\"水瓶星泉🌊\";", "STRING", "水瓶星泉🌊")]
    [InlineData("如果 (假) { 1; };", "NULL", "null")]
    [InlineData("中斷;", "BREAK_OBJ", "中斷")]
    [InlineData("[1, 2, 3][1];", "INTEGER", "2")]
    [InlineData("{\"星泉\": 42}[\"星泉\"];", "INTEGER", "42")]
    [InlineData("變數 a = [1]; a[0] = 42; a[0];", "INTEGER", "42")]
    [InlineData("變數 a = 2; a += 4; a *= 8; a -= 6; a /= 3; a;", "INTEGER", "14")]
    [InlineData("變數 a = 40; [a++, ++a, a];", "ARRAY", "[40, 42, 42]")]
    [InlineData("如果 (假) { 1; } 否則如果 (真) { 42; } 否則 { 3; };", "INTEGER", "42")]
    [InlineData("回傳 42; 0;", "INTEGER", "42")]
    [InlineData("變數 f = 函式(x) { 函式(y) { x + y; }; }; f(2)(40);", "INTEGER", "42")]
    [InlineData("變數 f = 函式(x) { 如果 (x == 0) { 回傳 42; } f(x-1); }; f(1000);", "INTEGER", "42")]
    [InlineData("變數 n = 0; 迴圈 (變數 i=0; i<5; i++) { 如果 (i == 3) { 中斷; } n += i; } n;", "INTEGER", "3")]
    [InlineData("變數 n = 0; 迴圈 (變數 i=0; i<3; i++) { 迴圈 (變數 j=0; j<5; j++) { 如果 (j==2) { 中斷; } n++; } } n;", "INTEGER", "6")]
    [InlineData("變數 f = 函式() { 中斷; 9; }; f();", "BREAK_OBJ", "中斷")]
    [InlineData("變數 f = 函式() { 迴圈 (變數 i=0; i<3; i++) { 回傳 i; } }; f();", "INTEGER", "0")]
    [InlineData("1 && 2;", "ERROR", "ERROR: Unknown operator: INTEGER && INTEGER")]
    [InlineData("未知;", "ERROR", "ERROR: Identifier not found: 未知")]
    public void RoundTripPreservesLanguageBehavior(string source, string type, string value) {
        var result = new VirtualMachine().Execute(RoundTrip(source));
        Assert.Equal(type, result.Type());
        Assert.Equal(value, result.Inspect());
    }

    [Theory]
    [InlineData("")]
    [InlineData("變數 n = 42;")]
    public void EmptyAndDeclarationOnlyProgramsPreserveVoid(string source) => Assert.Null(new VirtualMachine().Execute(RoundTrip(source)));

    [Fact]
    public void RoundTripPreservesDisassemblyAndFunctionInspectionWithoutAstParsing() {
        const string source = "變數 加 = 函式(甲) { 函式(乙) { 回傳 甲 + 乙; }; }; 加;";
        var original = new VmCompiler().Compile(source);
        var restored = RoundTrip(source);
        Assert.Equal(original.Disassemble(), restored.Disassemble());
        var function = Assert.IsType<FunctionObj>(new VirtualMachine().Execute(restored));
        Assert.Null(function.Body); // Persisted functions execute registered instructions, without reconstructing an AST.
        Assert.Equal(new VirtualMachine().Execute(original).Inspect(), function.Inspect());
        var inner = new VirtualMachine().Invoke(function, new IntegerObj(2));
        Assert.Equal(42, Assert.IsType<IntegerObj>(new VirtualMachine().Invoke(inner, new IntegerObj(40))).Value);
    }

    [Fact]
    public void LoadedCodeCanBeReserializedAndReusedWithIndependentState() {
        var program = RoundTrip("變數 a = [1]; a[0] = a[0] + 1; a;");
        using var output = new MemoryStream();
        BytecodeSerializer.Write(program, output);
        output.Position = 0;
        program = BytecodeSerializer.Read(output);
        var vm = new VirtualMachine();
        var first = Assert.IsType<ArrayObj>(vm.Execute(program));
        first.Elements[0] = new IntegerObj(100);
        Assert.Equal(2, Assert.IsType<IntegerObj>(Assert.IsType<ArrayObj>(vm.Execute(program)).Elements[0]).Value);
    }

    [Fact]
    public void ModuleMemberResolverBytecodeSurvivesRoundTrip() {
        var module = AquaEnvironment.NewEnvironment();
        VmEvaluator.NewInstance(new Builtins()).Evaluate("變數 建立 = 函式() { 函式(x) { x + 2; }; };", module);
        var caller = AquaEnvironment.NewEnvironment();
        caller.Create("模組", new ModuleObj(module));
        var program = RoundTrip("模組.建立()(40);");
        Assert.Equal(42, Assert.IsType<IntegerObj>(new VirtualMachine().Execute(program, caller)).Value);
    }

    [Fact]
    public void EveryTruncatedPrefixIsRejectedAndStreamsRemainOpen() {
        using var stream = new MemoryStream();
        BytecodeSerializer.Write(new VmCompiler().Compile("變數 f=函式(x) { x+2; }; f(40);"), stream);
        byte[] data = stream.ToArray();
        for (int length = 0; length < data.Length; length++) {
            using var truncated = new MemoryStream(data, 0, length);
            Assert.Throws<InvalidDataException>(() => BytecodeSerializer.Read(truncated));
            Assert.True(truncated.CanRead);
        }
        Assert.True(stream.CanWrite);
    }

    [Theory]
    [InlineData(0, 0)] // magic
    [InlineData(4, 99)] // version
    [InlineData(8, 2)] // wrap-return boolean
    [InlineData(9, 255)] // oversized/truncated instruction count
    [InlineData(13, 255)] // unknown opcode
    [InlineData(14, 255)] // invalid pool reference
    [InlineData(28, 255)] // oversized/truncated constant count
    [InlineData(32, 255)] // unknown constant tag
    public void CorruptHeadersAndInstructionsAreRejected(int offset, byte value) {
        using var stream = new MemoryStream();
        BytecodeSerializer.Write(new VmCompiler().Compile("42; 43;"), stream);
        var data = stream.ToArray();
        data[offset] = value;
        Assert.Throws<InvalidDataException>(() => BytecodeSerializer.Read(new MemoryStream(data)));
    }

    [Fact]
    public void TrailingDataIsRejected() {
        using var stream = new MemoryStream();
        BytecodeSerializer.Write(new VmCompiler().Compile("42;"), stream);
        stream.WriteByte(0);
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => BytecodeSerializer.Read(stream));
    }

    [Theory]
    [InlineData(OpCode.Hash, 1, 2, "Unusable as hash key")]
    [InlineData(OpCode.WriteIndex, 0, 3, "requires an array")]
    public void LoadedInstructionsWithWrongRuntimeTypesReturnLanguageErrors(OpCode opcode, int operand, int values, string expected) {
        // Valid instruction/stack structure can still contain values with invalid runtime types.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
            writer.Write(Encoding.ASCII.GetBytes("RIUS")); writer.Write(1); writer.Write(false);
            writer.Write(values + 1);
            for (int i = 0; i < values; i++) { writer.Write((byte)OpCode.Null); writer.Write(0); }
            writer.Write((byte)opcode); writer.Write(operand); writer.Write(0);
        }
        stream.Position = 0;
        var error = Assert.IsType<ErrorObj>(new VirtualMachine().Execute(BytecodeSerializer.Read(stream)));
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData(OpCode.Pop, 0)]
    [InlineData(OpCode.Call, 5)]
    [InlineData(OpCode.Jump, -1)]
    [InlineData(OpCode.Jump, 2)]
    [InlineData(OpCode.LeaveLoop, 0)]
    [InlineData(OpCode.Break, 0)]
    [InlineData(OpCode.Array, -1)]
    public void MalformedStackAndLoopInstructionsCannotReachTheVm(OpCode code, int operand) {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
            writer.Write(Encoding.ASCII.GetBytes("RIUS")); writer.Write(1); writer.Write(false);
            writer.Write(1); writer.Write((byte)code); writer.Write(operand); writer.Write(0);
        }
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => BytecodeSerializer.Read(stream));
    }
}
