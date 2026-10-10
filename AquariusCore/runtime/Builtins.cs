using System.Collections.Generic;
using AquariusLang.Object;
using AquariusLang.utils;

namespace AquariusLang.runtime {

    public class Builtins {
        protected Dictionary<string, BuiltinObj> builtinFuncs;
        protected Dictionary<string, IObject> builtins;

        public Builtins(Dictionary<string, BuiltinObj> builtinFuncs) {
            this.builtinFuncs = builtinFuncs;
            builtins = new Dictionary<string, IObject>();
        }

        public Builtins() {
            builtinFuncs = new Dictionary<string, BuiltinObj>();
            builtins = new Dictionary<string, IObject>();
        }

        protected static ErrorObj newError(string msg) {
            return new ErrorObj(msg);
        }

        public Dictionary<string, BuiltinObj> BuiltinFuncs => builtinFuncs;

        public void DefineFunction(BuiltinObj function, string? traditionalChineseName = null, string? englishName = null) =>
            FunctionRegistration.Define(builtinFuncs, function, traditionalChineseName, englishName);
        public Dictionary<string, IObject> _Builtins => builtins;
    }
}
