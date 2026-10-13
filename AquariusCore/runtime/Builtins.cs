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
            DefineFunction(BuiltinObj.FromBorrowed(args => args.Length == 0 ? new BooleanObj(false) : newError("isOSAndroid expects 0 arguments.")), "是Android", "isOSAndroid");
            DefineFunction(BuiltinObj.FromBorrowed(args => args.Length == 0 ? new BooleanObj(false) : newError("isWeb expects 0 arguments.")), "是網頁", "isWeb");
        }

        public Builtins() : this(new Dictionary<string, BuiltinObj>()) { }

        protected static ErrorObj newError(string msg) {
            return new ErrorObj(msg);
        }

        public Dictionary<string, BuiltinObj> BuiltinFuncs => builtinFuncs;

        public void DefineFunction(BuiltinObj function, string? traditionalChineseName = null, string? englishName = null) =>
            FunctionRegistration.Define(builtinFuncs, function, traditionalChineseName, englishName);
        public Dictionary<string, IObject> _Builtins => builtins;
    }
}
