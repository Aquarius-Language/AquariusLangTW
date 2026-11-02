using System;
using System.Linq;

namespace AquariusLang.Wasm;

/// <summary>
/// The portable 數學 / Math module as the native tier sees it: its functions, arities and constants exactly as
/// runtime/guest/maths.h declares them (a test keeps them equal), and the result type of a call. Native code computes
/// the pure functions itself (Wasm instructions, or the guest's own <c>rt_math</c> for the transcendental ones), so a
/// native call gives the generic call's exact result; Random, RandomInt and Seed (generator state, the host clock) stay
/// generic.
/// </summary>
internal static class WasmMath
{
    /// <summary>MATH_FIRST_ID: the builtin id of the first function.</summary>
    internal const int FirstId = 60;
    internal enum Function {
        Abs, Sign, Min, Max, Clamp, Floor, Ceil, Round, Trunc, FloorInt, CeilInt, RoundInt, TruncInt, Sqrt, Cbrt, Hypot, Pow, Exp, Log,
        Log2, Log10, Sin, Cos, Tan, Asin, Acos, Atan, Atan2, Sinh, Cosh, Tanh, IsNaN, IsFinite, Fmod, Random, RandomInt, Seed
    }
    internal static readonly (string English, string Chinese, int Arity)[] Functions = [
        ("Abs", "絕對值", 1), ("Sign", "正負號", 1), ("Min", "最小值", 2), ("Max", "最大值", 2), ("Clamp", "限制範圍", 3), ("Floor", "向下取整", 1),
        ("Ceil", "向上取整", 1), ("Round", "四捨五入", 1), ("Trunc", "截斷", 1), ("FloorInt", "向下取整數", 1), ("CeilInt", "向上取整數", 1),
        ("RoundInt", "四捨五入取整數", 1), ("TruncInt", "截斷取整數", 1), ("Sqrt", "平方根", 1), ("Cbrt", "立方根", 1), ("Hypot", "斜邊長", 2),
        ("Pow", "次方", 2), ("Exp", "指數", 1), ("Log", "對數", 1), ("Log2", "以二為底對數", 1), ("Log10", "以十為底對數", 1), ("Sin", "正弦", 1),
        ("Cos", "餘弦", 1), ("Tan", "正切", 1), ("Asin", "反正弦", 1), ("Acos", "反餘弦", 1), ("Atan", "反正切", 1), ("Atan2", "雙參數反正切", 2),
        ("Sinh", "雙曲正弦", 1), ("Cosh", "雙曲餘弦", 1), ("Tanh", "雙曲正切", 1), ("IsNaN", "是非數", 1), ("IsFinite", "是有限數", 1),
        ("Fmod", "浮點餘數", 2), ("Random", "亂數", 0), ("RandomInt", "亂數整數", 2), ("Seed", "亂數種子", 1)];
    /// <summary>The module's double constants (math_constants); NaN is the guest's positive quiet NaN.</summary>
    internal static readonly (string English, string Chinese, double Value)[] Constants = [
        ("PI", "圓周率", Math.PI), ("E", "自然常數", Math.E), ("TAU", "二倍圓周率", 2 * Math.PI), ("Infinity", "無限大", double.PositiveInfinity),
        ("NaN", "非數", BitConverter.Int64BitsToDouble(0x7ff8000000000000))];

    internal static bool IsModule(string name) => name is "Math" or "數學";
    internal static int FunctionIndex(string name) => Array.FindIndex(Functions, f => f.English == name || f.Chinese == name);
    internal static int ConstantIndex(string name) => Array.FindIndex(Constants, c => c.English == name || c.Chinese == name);

    /// <summary>The result type of a native call of function k with arguments of the native number types (1 integer,
    /// 2 float, 3 double), or 0 when native code leaves the call to the runtime (another argument, count or function).
    /// Like the generic call: Abs to Trunc keep the widest argument type, the *Int functions give an integer, IsNaN and
    /// IsFinite a Boolean (4), the others a double, or a float for float arguments.</summary>
    internal static int ResultType(int k, int[] arguments)
    {
        const int Int = 1, Float = 2, Double = 3, Bool = 4;
        if (k < 0 || k >= Functions.Length || arguments.Length != Functions[k].Arity || arguments.Any(a => a is not (Int or Float or Double))) return 0;
        int widest = arguments.DefaultIfEmpty(Int).Max();
        return (Function)k switch {
            Function.Random or Function.RandomInt or Function.Seed => 0,
            <= Function.Trunc => widest,
            <= Function.TruncInt => Int,
            Function.IsNaN or Function.IsFinite => Bool,
            _ => widest == Float ? Float : Double };
    }
}
