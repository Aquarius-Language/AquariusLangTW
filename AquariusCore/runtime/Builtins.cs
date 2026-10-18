using System.Collections.Generic;
using System;
using System.Globalization;
using System.Diagnostics;
using AquariusLang.Object;
using AquariusLang.utils;

namespace AquariusLang.runtime {

    public class Builtins {
        private static readonly long clockOrigin = Stopwatch.GetTimestamp();
        protected Dictionary<string, BuiltinObj> builtinFuncs;
        protected Dictionary<string, IObject> builtins;

        public Builtins(Dictionary<string, BuiltinObj> builtinFuncs) {
            this.builtinFuncs = builtinFuncs;
            builtins = new Dictionary<string, IObject>();
            // 純量主機函式不保留或修改呼叫者範圍，避免讀取時間時回寫整份文件。
            var 純量範圍 = AquariusLang.Object.Environment.NewEnvironment();
            var 時間函式 = BuiltinObj.FromBorrowed(args => args.Length == 0
                ? new DoubleObj(Stopwatch.GetElapsedTime(clockOrigin).TotalMilliseconds)
                : newError($"currentTime / 目前時間: Expected 0 arguments, got {args.Length}."));
            時間函式.RetainedEnvironment = 純量範圍;
            DefineFunction(時間函式,
                traditionalChineseName: "目前時間", englishName: "currentTime");
            var 日期函式 = BuiltinObj.FromBorrowed(args => args.Length == 0
                ? new StringObj(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                : newError($"currentDate / 目前日期: Expected 0 arguments, got {args.Length}."));
            日期函式.RetainedEnvironment = 純量範圍;
            DefineFunction(日期函式,
                traditionalChineseName: "目前日期", englishName: "currentDate");
            var 型別函式 = BuiltinObj.FromBorrowed(args => args.Length == 1
                ? new StringObj(TypeName(args[0]))
                : newError($"typeOf / 型別: Expected 1 argument, got {args.Length}."));
            型別函式.RetainedEnvironment = 純量範圍; 型別函式.PreservesArguments = true;
            DefineFunction(型別函式, traditionalChineseName: "型別", englishName: "typeOf");
            DefineFunction(BuiltinObj.FromBorrowed(args => args.Length == 0 ? new BooleanObj(false) : newError("isOSAndroid expects 0 arguments.")), "是Android", "isOSAndroid");
            DefineFunction(BuiltinObj.FromBorrowed(args => args.Length == 0 ? new BooleanObj(false) : newError("isWeb expects 0 arguments.")), "是網頁", "isWeb");
            DefineFunction(BuiltinObj.FromPortable(5, _ => newError("floodFill requires compiled Wasm execution.")), "區域填色", "floodFill");
        }

        public Builtins() : this(new Dictionary<string, BuiltinObj>()) { }

        /// <summary>Stable type tags shared by every host: integer, float, double, string, boolean, null,
        /// array, hash, function, module and error. Both 型別 and typeOf return these English tags.</summary>
        public static string TypeName(IObject value) => value switch {
            IntegerObj => "integer", FloatObj => "float", DoubleObj => "double", StringObj => "string", BooleanObj => "boolean",
            null or NullObj => "null", ArrayObj => "array", HashObj => "hash", FunctionObj or BuiltinObj => "function",
            ModuleObj => "module", ErrorObj => "error", _ => value.Type().ToLowerInvariant()
        };

        protected static ErrorObj newError(string msg) {
            return new ErrorObj(msg);
        }

        public Dictionary<string, BuiltinObj> BuiltinFuncs => builtinFuncs;

        public void DefineFunction(BuiltinObj function, string? traditionalChineseName = null, string? englishName = null) =>
            FunctionRegistration.Define(builtinFuncs, function, traditionalChineseName, englishName);
        public Dictionary<string, IObject> _Builtins => builtins;
    }
}
