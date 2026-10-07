using System.Collections.Generic;
using AquariusLang.Object;
using AquariusLang.utils;

namespace AquariusLang.evaluator {

    public class Builtins {
        protected Dictionary<string, BuiltinObj> builtinFuncs;
        protected Dictionary<string, IObject> builtins;

        public Builtins(Dictionary<string, BuiltinObj> builtinFuncs) {
            this.builtinFuncs = builtinFuncs;
        }

        public Builtins() {
            builtinFuncs = new Dictionary<string, BuiltinObj>();
            builtins = new Dictionary<string, IObject>();
        }

        protected static ErrorObj newError(string msg) {
            return new ErrorObj(msg);
        }

        public Dictionary<string, BuiltinObj> BuiltinFuncs => builtinFuncs;
        public Dictionary<string, IObject> _Builtins => builtins;
    }
}
