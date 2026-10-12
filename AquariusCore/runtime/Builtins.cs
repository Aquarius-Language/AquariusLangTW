using System.Collections.Generic;
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
            DefineFunction(BuiltinObj.FromBorrowed(args => args.Length == 0
                ? new DoubleObj(Stopwatch.GetElapsedTime(clockOrigin).TotalMilliseconds)
                : newError($"currentTime / 目前時間: Expected 0 arguments, got {args.Length}.")),
                traditionalChineseName: "目前時間", englishName: "currentTime");
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
