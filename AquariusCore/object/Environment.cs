using System.Collections.Generic;

namespace AquariusLang.Object {

    public class Environment {
        /// <summary>
        /// Any variables from global to local get stored here.
        /// </summary>
        private Dictionary<string, IObject> store;

        /// <summary>
        /// Only variables that are owned within this environment/scope. 
        /// </summary>
        private Dictionary<string, IObject> owned;
        private Environment outer;
        internal IEnumerable<IObject> StoredValues => store.Values;
        internal Environment Outer => outer;


        public static Environment NewEnclosedEnvironment(Environment outer) {
            Environment environment = new Environment()
                { store = new Dictionary<string, IObject>(), owned = new Dictionary<string, IObject>(), outer = outer };
            return environment;
        }

        public static Environment NewEnvironment() {
            Environment environment = new Environment()
                { store = new Dictionary<string, IObject>(), owned = new Dictionary<string, IObject>(), outer = null };
            return environment;
        }

        /// <summary>
        /// Keep finding variables from outter scope if the current scope doesn't have the variable.
        /// Search until no more outter scopes are available.
        ///
        /// This doesn't check if the gotten value is owned or stored.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public IObject Get(string name, out bool hasVar) {
            for (Environment scope = this; scope != null; scope = scope.outer) {
                if (scope.store.TryGetValue(name, out var value)) {
                    hasVar = true;
                    return value;
                }
            }

            hasVar = false;
            return null;
        }

        /// <summary>
        /// Only return value of owned variables.
        /// </summary>
        /// <param name="name"></param>
        /// <returns>Variable value if exists. Otherwise, return null.</returns>
        public IObject GetOwned(string name) {
            return owned.TryGetValue(name, out var value) ? value : null;
        }

        public bool Owns(string name) {
            return owned.ContainsKey(name);
        }

        /// <summary>Public bindings of this module/scope, without inherited locals.</summary>
        public IReadOnlyDictionary<string, IObject> OwnedBindings => new System.Collections.ObjectModel.ReadOnlyDictionary<string, IObject>(owned);

        /// <summary>
        /// Keep setting reference variable value from nested outer scope, until
        /// the scope that owns the variable is found, then also update its value
        /// in "owned" dictionary.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="val"></param>
        public void Set(string name, IObject val) {
            Environment scope = this;
            while (!scope.Owns(name)) {
                scope.store[name] = val;
                scope = scope.outer;
            }

            scope.store[name] = val;
            scope.owned[name] = val;
        }

        /// <summary>
        /// Create a new variable that's owned by this environment (scope).
        /// </summary>
        /// <param name="name"></param>
        /// <param name="val"></param>
        public void Create(string name, IObject val) {
            owned[name] = val;
            store[name] = val;
        }
    }
}
