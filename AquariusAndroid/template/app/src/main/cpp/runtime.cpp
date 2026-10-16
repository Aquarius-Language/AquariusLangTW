#include <jni.h>
#include <quickjs.h>
#include <wasmtime.h>
#include <atomic>
#include <cstring>
#include <deque>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
struct Engine;
struct Binding { std::string name, signature; jmethodID method; std::vector<char> args; char result; };
struct External { wasmtime_extern_t value{}; JSValue buffer = JS_UNDEFINED; size_t size = 0; uint8_t* data = nullptr; };
struct Callback { Engine* engine; JSValue function; std::vector<wasm_valkind_t> results; };
struct Engine {
  JNIEnv* env;
  jobject bridge;
  jmethodID sourceMethod;
  JSRuntime* runtime = nullptr;
  JSContext* context = nullptr;
  wasm_engine_t* wasm = nullptr;
  wasmtime_store_t* store = nullptr;
  wasmtime_context_t* active = nullptr;
  std::atomic<bool> cancelled{false};
  std::vector<Binding> bindings;
  std::vector<wasmtime_module_t*> modules;
  std::deque<External> externals;
  std::vector<size_t> memories;
  std::vector<std::unique_ptr<Callback>> callbacks;
  JSValue hostError = JS_UNDEFINED;
  std::string rejection;
  explicit Engine(JNIEnv* env, jobject bridge) : env(env), bridge(env->NewGlobalRef(bridge)) {}
  ~Engine() {
    if (context) {
      for (auto& item : externals) { if (!JS_IsUndefined(item.buffer)) JS_DetachArrayBuffer(context, item.buffer); JS_FreeValue(context, item.buffer); }
      for (auto& callback : callbacks) JS_FreeValue(context, callback->function);
      JS_FreeValue(context, hostError);
      JS_FreeContext(context);
    }
    if (runtime) JS_FreeRuntime(runtime);
    if (store) wasmtime_store_delete(store);
    for (auto* module : modules) wasmtime_module_delete(module);
    if (wasm) wasm_engine_delete(wasm);
    env->DeleteGlobalRef(bridge);
  }
};
Engine* engine(JSContext* ctx) { return static_cast<Engine*>(JS_GetContextOpaque(ctx)); }
std::string text(JSContext* ctx, JSValueConst value) {
  size_t length; const char* chars = JS_ToCStringLen(ctx, &length, value);
  if (!chars) throw std::runtime_error("Cannot convert string");
  std::string result(chars, length); JS_FreeCString(ctx, chars); return result;
}
std::string errorText(JSContext* ctx, JSValueConst error) {
  auto stack = JS_GetPropertyStr(ctx, error, "stack");
  std::string result = text(ctx, error);
  if (!JS_IsUndefined(stack)) result += "\n" + text(ctx, stack);
  JS_FreeValue(ctx, stack); return result;
}
void checked(JSContext* ctx, JSValue value) {
  if (JS_IsException(value)) { auto exception = JS_GetException(ctx); auto message = errorText(ctx, exception); JS_FreeValue(ctx, exception); throw std::runtime_error(message); }
  JS_FreeValue(ctx, value);
}
jstring javaString(JSContext* ctx, JSValueConst value) {
  size_t length; auto* chars = JS_ToCStringLenUTF16(ctx, &length, value);
  if (!chars) throw std::runtime_error("Invalid Java string");
  jstring result = engine(ctx)->env->NewString(reinterpret_cast<const jchar*>(chars), length);
  JS_FreeCStringUTF16(ctx, chars); return result;
}
JSValue jsString(Engine* e, jstring value) {
  if (!value) return JS_NULL;
  auto* chars = e->env->GetStringChars(value, nullptr);
  auto result = JS_NewStringUTF16(e->context, reinterpret_cast<const uint16_t*>(chars), e->env->GetStringLength(value));
  e->env->ReleaseStringChars(value, chars); return result;
}
void javaError(Engine* e) {
  if (!e->env->ExceptionCheck()) return;
  auto error = e->env->ExceptionOccurred(); e->env->ExceptionClear();
  auto cls = e->env->GetObjectClass(error);
  auto method = e->env->GetMethodID(cls, "toString", "()Ljava/lang/String;");
  auto message = static_cast<jstring>(e->env->CallObjectMethod(error, method));
  auto value = jsString(e, message); auto result = text(e->context, value); JS_FreeValue(e->context, value);
  e->env->DeleteLocalRef(message); e->env->DeleteLocalRef(cls); e->env->DeleteLocalRef(error);
  throw std::runtime_error(result);
}
std::string source(Engine* e, const char* name) {
  auto path = e->env->NewStringUTF(name);
  auto value = static_cast<jstring>(e->env->CallObjectMethod(e->bridge, e->sourceMethod, path));
  e->env->DeleteLocalRef(path); javaError(e);
  auto js = jsString(e, value); auto result = text(e->context, js);
  JS_FreeValue(e->context, js); e->env->DeleteLocalRef(value); return result;
}
struct Bytes {
  JSContext* ctx; JSValue buffer = JS_UNDEFINED; uint8_t* data; size_t length;
  Bytes(JSContext* ctx, JSValueConst value) : ctx(ctx) {
    size_t offset = 0;
    if (JS_IsArrayBuffer(value)) buffer = JS_DupValue(ctx, value);
    else buffer = JS_GetTypedArrayBuffer(ctx, value, &offset, &length, nullptr);
    if (JS_IsException(buffer)) { JS_FreeValue(ctx, JS_GetException(ctx)); throw std::runtime_error("Expected an ArrayBuffer or typed array"); }
    size_t capacity = 0; data = JS_GetArrayBuffer(ctx, &capacity, buffer);
    if (JS_IsArrayBuffer(value)) length = capacity;
    if (offset > capacity || length > capacity - offset) throw std::runtime_error("Invalid buffer range");
    if (data) data += offset;
  }
  ~Bytes() { JS_FreeValue(ctx, buffer); }
};
JSValue javaCall(JSContext* ctx, JSValueConst, int argc, JSValueConst* argv, int magic) {
  auto* e = engine(ctx); auto& binding = e->bindings.at(magic);
  e->env->PushLocalFrame(32);
  try {
    if (argc < static_cast<int>(binding.args.size())) throw std::runtime_error("Missing native arguments: " + binding.name);
    std::vector<jvalue> args(binding.args.size()); std::vector<std::unique_ptr<Bytes>> buffers;
    for (size_t i = 0; i < binding.args.size(); ++i) {
      switch (binding.args[i]) {
        case 'I': JS_ToInt32(ctx, &args[i].i, argv[i]); break;
        case 'D': JS_ToFloat64(ctx, &args[i].d, argv[i]); break;
        case 'Z': args[i].z = JS_ToBool(ctx, argv[i]); break;
        case 'B': { auto bytes = std::make_unique<Bytes>(ctx, argv[i]); args[i].l = e->env->NewDirectByteBuffer(bytes->data, bytes->length); buffers.push_back(std::move(bytes)); break; }
        default: args[i].l = javaString(ctx, argv[i]);
      }
    }
    JSValue result = JS_UNDEFINED;
    switch (binding.result) {
      case 'V': e->env->CallVoidMethodA(e->bridge, binding.method, args.data()); break;
      case 'I': result = JS_NewInt32(ctx, e->env->CallIntMethodA(e->bridge, binding.method, args.data())); break;
      case 'D': result = JS_NewFloat64(ctx, e->env->CallDoubleMethodA(e->bridge, binding.method, args.data())); break;
      case 'Z': result = JS_NewBool(ctx, e->env->CallBooleanMethodA(e->bridge, binding.method, args.data())); break;
      default: {
        auto object = e->env->CallObjectMethodA(e->bridge, binding.method, args.data()); javaError(e);
        if (binding.result == '[' && object) {
          auto array = static_cast<jbyteArray>(object); size_t length = e->env->GetArrayLength(array);
          auto* bytes = e->env->GetByteArrayElements(array, nullptr);
          auto buffer = JS_NewArrayBufferCopy(ctx, reinterpret_cast<uint8_t*>(bytes), length);
          e->env->ReleaseByteArrayElements(array, bytes, JNI_ABORT);
          result = JS_NewTypedArray(ctx, 1, &buffer, JS_TYPED_ARRAY_UINT8); JS_FreeValue(ctx, buffer);
        } else result = jsString(e, static_cast<jstring>(object));
      }
    }
    javaError(e); e->env->PopLocalFrame(nullptr); return result;
  } catch (const std::exception& ex) { e->env->PopLocalFrame(nullptr); return JS_ThrowInternalError(ctx, "%s", ex.what()); }
}

const char* kinds[] = {"function", "global", "table", "memory"};
void wasmError(wasmtime_error_t* error, wasm_trap_t* trap = nullptr) {
  if (!error && !trap) return;
  wasm_byte_vec_t message;
  if (error) wasmtime_error_message(error, &message); else wasm_trap_message(trap, &message);
  std::string result(message.data, message.size); wasm_byte_vec_delete(&message);
  if (error) wasmtime_error_delete(error); if (trap) wasm_trap_delete(trap);
  throw std::runtime_error(result);
}
uint32_t index(JSContext* ctx, JSValueConst value, size_t size) {
  int64_t id; if (JS_ToInt64(ctx, &id, value) < 0 || id < 0 || static_cast<uint64_t>(id) >= size) throw std::runtime_error("Invalid Wasmtime handle"); return id;
}
External& external(JSContext* ctx, JSValueConst value, int kind = -1) {
  auto& item = engine(ctx)->externals.at(index(ctx, value, engine(ctx)->externals.size()));
  if (kind >= 0 && item.value.kind != kind) throw std::runtime_error("Wrong Wasmtime handle type"); return item;
}
// Guest code can grow memory before a host callback or a normal/trapping return.
// Detach old views at those boundaries, before JavaScript can observe stale storage.
void refreshMemories(Engine* e) {
  for (size_t id : e->memories) {
    auto& item = e->externals[id];
    if (JS_IsUndefined(item.buffer)) continue;
    auto* data = wasmtime_memory_data(e->active, &item.value.of.memory);
    auto size = wasmtime_memory_data_size(e->active, &item.value.of.memory);
    if (data == item.data && size == item.size) continue;
    JS_DetachArrayBuffer(e->context, item.buffer); JS_FreeValue(e->context, item.buffer);
    item.buffer = JS_UNDEFINED;
  }
}
JSValue record(Engine* e, const wasmtime_extern_t& value) {
  if (value.kind > WASMTIME_EXTERN_MEMORY) throw std::runtime_error("Unsupported WebAssembly export kind");
  size_t id = 0;
  for (; id < e->externals.size(); ++id) {
    const auto& other = e->externals[id].value;
    if (other.kind != value.kind) continue;
    if (value.kind == WASMTIME_EXTERN_FUNC && other.of.func.store_id == value.of.func.store_id && other.of.func.__private == value.of.func.__private) break;
    if (value.kind == WASMTIME_EXTERN_MEMORY && memcmp(&other.of.memory, &value.of.memory, sizeof(value.of.memory)) == 0) break;
    if (value.kind == WASMTIME_EXTERN_TABLE && memcmp(&other.of.table, &value.of.table, sizeof(value.of.table)) == 0) break;
    if (value.kind == WASMTIME_EXTERN_GLOBAL && memcmp(&other.of.global, &value.of.global, sizeof(value.of.global)) == 0) break;
  }
  auto result = JS_NewObject(e->context); JS_SetPropertyStr(e->context, result, "id", JS_NewInt64(e->context, id));
  JS_SetPropertyStr(e->context, result, "kind", JS_NewString(e->context, kinds[value.kind]));
  if (id == e->externals.size()) {
    e->externals.push_back(External{value});
    if (value.kind == WASMTIME_EXTERN_MEMORY) e->memories.push_back(id);
  }
  return result;
}
JSValue fromWasm(Engine* e, const wasmtime_val_t& value) {
  auto* ctx = e->context;
  switch (value.kind) {
    case WASMTIME_I32: return JS_NewInt32(ctx, value.of.i32);
    case WASMTIME_I64: return JS_NewBigInt64(ctx, value.of.i64);
    case WASMTIME_F32: return JS_NewFloat64(ctx, value.of.f32);
    case WASMTIME_F64: return JS_NewFloat64(ctx, value.of.f64);
    case WASMTIME_FUNCREF: { if (!value.of.funcref.store_id) return JS_NULL; wasmtime_extern_t ext{}; ext.kind = WASMTIME_EXTERN_FUNC; ext.of.func = value.of.funcref; return record(e, ext); }
    default: throw std::runtime_error("Unsupported WebAssembly value");
  }
}
wasmtime_val_t toWasm(Engine* e, JSValueConst value, wasm_valkind_t kind) {
  wasmtime_val_t result{};
  switch (kind) {
    case WASM_I32: result.kind = WASMTIME_I32; if (JS_ToInt32(e->context, &result.of.i32, value)) throw std::runtime_error("Expected i32"); break;
    case WASM_I64: result.kind = WASMTIME_I64; if (JS_ToBigInt64(e->context, &result.of.i64, value)) throw std::runtime_error("Expected i64 BigInt"); break;
    case WASM_F32: { double n; if (JS_ToFloat64(e->context, &n, value)) throw std::runtime_error("Expected f32"); result.kind = WASMTIME_F32; result.of.f32 = n; break; }
    case WASM_F64: result.kind = WASMTIME_F64; if (JS_ToFloat64(e->context, &result.of.f64, value)) throw std::runtime_error("Expected f64"); break;
    case WASM_FUNCREF: result.kind = WASMTIME_FUNCREF; if (!JS_IsNull(value)) result.of.funcref = external(e->context, value, WASMTIME_EXTERN_FUNC).value.of.func; break;
    default: throw std::runtime_error("Unsupported WebAssembly value type");
  }
  return result;
}
wasm_trap_t* callback(void* data, wasmtime_caller_t* caller, const wasmtime_val_t* args, size_t nargs, wasmtime_val_t* results, size_t nresults) {
  auto* cb = static_cast<Callback*>(data); auto* e = cb->engine; auto* previous = e->active;
  e->active = wasmtime_caller_context(caller);
  try {
    refreshMemories(e);
    std::vector<JSValue> values; for (size_t i = 0; i < nargs; ++i) values.push_back(fromWasm(e, args[i]));
    auto result = JS_Call(e->context, cb->function, JS_UNDEFINED, values.size(), values.data());
    for (auto value : values) JS_FreeValue(e->context, value);
    if (JS_IsException(result)) { JS_FreeValue(e->context, e->hostError); e->hostError = JS_GetException(e->context); throw std::runtime_error(errorText(e->context, e->hostError)); }
    try {
      for (size_t i = 0; i < nresults; ++i) {
        auto value = nresults == 1 ? JS_DupValue(e->context, result) : JS_GetPropertyUint32(e->context, result, i);
        results[i] = toWasm(e, value, cb->results[i]); JS_FreeValue(e->context, value);
      }
    } catch (...) { JS_FreeValue(e->context, result); throw; }
    JS_FreeValue(e->context, result); e->active = previous; return nullptr;
  } catch (const std::exception& ex) { e->active = previous; return wasmtime_trap_new(ex.what(), strlen(ex.what())); }
}

enum Operation { Compile, Describe, Instantiate, Call, Memory, Buffer, GrowMemory, Table, TableSize, TableGet, TableSet, TableGrow, Global, GlobalGet, GlobalSet };
JSValue wasmCall(JSContext* ctx, JSValueConst, int, JSValueConst* argv, int operation) {
  auto* e = engine(ctx);
  try {
    if (e->cancelled) throw std::runtime_error("Runtime cancelled");
    if (operation == Compile) {
      Bytes data(ctx, argv[0]); wasmtime_module_t* module;
      wasmError(wasmtime_module_new(e->wasm, data.data, data.length, &module));
      e->modules.push_back(module); return JS_NewInt64(ctx, e->modules.size() - 1);
    }
    if (operation == Describe || operation == Instantiate) {
      auto* module = e->modules.at(index(ctx, argv[0], e->modules.size()));
      wasm_importtype_vec_t imports; wasmtime_module_imports(module, &imports);
      wasm_exporttype_vec_t exports; wasmtime_module_exports(module, &exports);
      auto array = JS_NewArray(ctx);
      try {
        if (operation == Describe) {
          bool importing = JS_ToBool(ctx, argv[1]); size_t size = importing ? imports.size : exports.size;
          for (size_t i = 0; i < size; ++i) {
            auto* type = importing ? wasm_importtype_type(imports.data[i]) : wasm_exporttype_type(exports.data[i]);
            auto* name = importing ? wasm_importtype_name(imports.data[i]) : wasm_exporttype_name(exports.data[i]);
            auto item = JS_NewObject(ctx); JS_SetPropertyStr(ctx, item, "name", JS_NewStringLen(ctx, name->data, name->size));
            auto kind = wasm_externtype_kind(type);
            if (kind > WASM_EXTERN_MEMORY) { JS_FreeValue(ctx, item); throw std::runtime_error("Unsupported WebAssembly import/export kind"); }
            JS_SetPropertyStr(ctx, item, "kind", JS_NewString(ctx, kinds[kind]));
            if (importing) { auto* from = wasm_importtype_module(imports.data[i]); JS_SetPropertyStr(ctx, item, "module", JS_NewStringLen(ctx, from->data, from->size)); }
            JS_SetPropertyUint32(ctx, array, i, item);
          }
        } else {
          std::vector<wasmtime_extern_t> values;
          for (size_t i = 0; i < imports.size; ++i) {
            auto value = JS_GetPropertyUint32(ctx, argv[1], i);
            auto* type = wasm_importtype_type(imports.data[i]); wasmtime_extern_t ext{};
            if (wasm_externtype_kind(type) == WASM_EXTERN_FUNC && JS_IsFunction(ctx, value)) {
              auto* functionType = wasm_externtype_as_functype_const(type);
              auto cb = std::make_unique<Callback>(); cb->engine = e; cb->function = JS_DupValue(ctx, value);
              auto* returns = wasm_functype_results(functionType); for (size_t n = 0; n < returns->size; ++n) cb->results.push_back(wasm_valtype_kind(returns->data[n]));
              ext.kind = WASMTIME_EXTERN_FUNC; wasmtime_func_new(e->active, functionType, callback, cb.get(), nullptr, &ext.of.func); e->callbacks.push_back(std::move(cb));
            } else ext = external(ctx, value, wasm_externtype_kind(type)).value;
            JS_FreeValue(ctx, value); values.push_back(ext);
          }
          wasmtime_instance_t instance; wasm_trap_t* trap = nullptr;
          auto* error = wasmtime_instance_new(e->active, module, values.data(), values.size(), &instance, &trap); refreshMemories(e); wasmError(error, trap);
          for (size_t i = 0; i < exports.size; ++i) {
            auto* name = wasm_exporttype_name(exports.data[i]); wasmtime_extern_t value;
            if (!wasmtime_instance_export_get(e->active, &instance, name->data, name->size, &value)) throw std::runtime_error("Missing instance export");
            auto item = record(e, value); JS_SetPropertyStr(ctx, item, "name", JS_NewStringLen(ctx, name->data, name->size)); JS_SetPropertyUint32(ctx, array, i, item);
          }
        }
      } catch (...) { JS_FreeValue(ctx, array); wasm_importtype_vec_delete(&imports); wasm_exporttype_vec_delete(&exports); throw; }
      wasm_importtype_vec_delete(&imports); wasm_exporttype_vec_delete(&exports); return array;
    }
    if (operation == Call) {
      auto function = external(ctx, argv[0], WASMTIME_EXTERN_FUNC).value.of.func;
      auto* type = wasmtime_func_type(e->active, &function); auto* params = wasm_functype_params(type); auto* returns = wasm_functype_results(type);
      std::vector<wasmtime_val_t> args, results(returns->size);
      try {
        for (size_t i = 0; i < params->size; ++i) { auto value = JS_GetPropertyUint32(ctx, argv[1], i); args.push_back(toWasm(e, value, wasm_valtype_kind(params->data[i]))); JS_FreeValue(ctx, value); }
        wasm_trap_t* trap = nullptr; auto* error = wasmtime_func_call(e->active, &function, args.data(), args.size(), results.data(), results.size(), &trap);
        refreshMemories(e); wasmError(error, trap);
      } catch (...) { wasm_functype_delete(type); throw; }
      wasm_functype_delete(type);
      if (results.empty()) return JS_UNDEFINED;
      if (results.size() == 1) return fromWasm(e, results[0]);
      auto array = JS_NewArray(ctx); for (size_t i = 0; i < results.size(); ++i) JS_SetPropertyUint32(ctx, array, i, fromWasm(e, results[i])); return array;
    }
    if (operation == Memory || operation == Table || operation == Global) {
      wasmtime_extern_t value{};
      if (operation == Memory) {
        int64_t initial, maximum = 0; JS_ToInt64(ctx, &initial, argv[0]); if (!JS_IsUndefined(argv[1])) JS_ToInt64(ctx, &maximum, argv[1]);
        wasm_memorytype_t* type; wasmError(wasmtime_memorytype_new(initial, !JS_IsUndefined(argv[1]), maximum, false, false, 16, &type));
        value.kind = WASMTIME_EXTERN_MEMORY; auto* error = wasmtime_memory_new(e->active, type, &value.of.memory); wasm_memorytype_delete(type); wasmError(error);
      } else if (operation == Table) {
        uint32_t initial, maximum = UINT32_MAX; JS_ToUint32(ctx, &initial, argv[0]); if (!JS_IsUndefined(argv[1])) JS_ToUint32(ctx, &maximum, argv[1]);
        wasm_limits_t limits{initial, maximum}; auto* type = wasm_tabletype_new(wasm_valtype_new(WASM_FUNCREF), &limits); auto init = toWasm(e, argv[2], WASM_FUNCREF);
        value.kind = WASMTIME_EXTERN_TABLE; auto* error = wasmtime_table_new(e->active, type, &init, &value.of.table); wasm_tabletype_delete(type); wasmError(error);
      } else {
        auto name = text(ctx, argv[0]); wasm_valkind_t kind = name == "i32" ? WASM_I32 : name == "i64" ? WASM_I64 : name == "f32" ? WASM_F32 : WASM_F64;
        auto* type = wasm_globaltype_new(wasm_valtype_new(kind), JS_ToBool(ctx, argv[1]) ? WASM_VAR : WASM_CONST); auto init = toWasm(e, argv[2], kind);
        value.kind = WASMTIME_EXTERN_GLOBAL; auto* error = wasmtime_global_new(e->active, type, &init, &value.of.global); wasm_globaltype_delete(type); wasmError(error);
      }
      e->externals.push_back(External{value});
      if (value.kind == WASMTIME_EXTERN_MEMORY) e->memories.push_back(e->externals.size() - 1);
      return JS_NewInt64(ctx, e->externals.size() - 1);
    }
    if (operation == Buffer || operation == GrowMemory) {
      auto& memory = external(ctx, argv[0], WASMTIME_EXTERN_MEMORY);
      if (operation == GrowMemory) {
        int64_t delta; JS_ToInt64(ctx, &delta, argv[1]); uint64_t previous;
        wasmError(wasmtime_memory_grow(e->active, &memory.value.of.memory, delta, &previous));
        if (!JS_IsUndefined(memory.buffer)) JS_DetachArrayBuffer(ctx, memory.buffer);
        JS_FreeValue(ctx, memory.buffer); memory.buffer = JS_UNDEFINED; return JS_NewInt64(ctx, previous);
      }
      auto* data = wasmtime_memory_data(e->active, &memory.value.of.memory); auto size = wasmtime_memory_data_size(e->active, &memory.value.of.memory);
      if (JS_IsUndefined(memory.buffer) || data != memory.data || size != memory.size) {
        if (!JS_IsUndefined(memory.buffer)) JS_DetachArrayBuffer(ctx, memory.buffer);
        JS_FreeValue(ctx, memory.buffer); memory.buffer = JS_NewArrayBuffer(ctx, data, size, 0, nullptr, nullptr, false); memory.data = data; memory.size = size;
      }
      return JS_DupValue(ctx, memory.buffer);
    }
    if (operation >= TableSize && operation <= TableGrow) {
      auto table = external(ctx, argv[0], WASMTIME_EXTERN_TABLE).value.of.table;
      if (operation == TableSize) return JS_NewInt64(ctx, wasmtime_table_size(e->active, &table));
      int64_t n; JS_ToInt64(ctx, &n, argv[1]);
      if (operation == TableGet) { wasmtime_val_t value; if (!wasmtime_table_get(e->active, &table, n, &value)) throw std::runtime_error("Table index out of bounds"); return fromWasm(e, value); }
      auto value = toWasm(e, argv[2], WASM_FUNCREF);
      if (operation == TableSet) { wasmError(wasmtime_table_set(e->active, &table, n, &value)); return JS_UNDEFINED; }
      uint64_t previous; wasmError(wasmtime_table_grow(e->active, &table, n, &value, &previous)); return JS_NewInt64(ctx, previous);
    }
    auto global = external(ctx, argv[0], WASMTIME_EXTERN_GLOBAL).value.of.global;
    if (operation == GlobalGet) { wasmtime_val_t value; wasmtime_global_get(e->active, &global, &value); return fromWasm(e, value); }
    auto* type = wasmtime_global_type(e->active, &global); auto kind = wasm_valtype_kind(wasm_globaltype_content(type)); wasm_globaltype_delete(type);
    auto value = toWasm(e, argv[1], kind); wasmError(wasmtime_global_set(e->active, &global, &value)); return JS_UNDEFINED;
  } catch (const std::exception& ex) {
    if (!JS_IsUndefined(e->hostError)) { auto value = e->hostError; e->hostError = JS_UNDEFINED; return JS_Throw(ctx, value); }
    return JS_ThrowInternalError(ctx, "%s", ex.what());
  }
}

char* normalize(JSContext* ctx, const char*, const char* name, void*) {
  if (strncmp(name, "./", 2) == 0) name += 2;
  if (!*name || strspn(name, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_.-") != strlen(name)) { JS_ThrowReferenceError(ctx, "Invalid runtime module: %s", name); return nullptr; }
  return js_strdup(ctx, name);
}
JSModuleDef* loadModule(JSContext* ctx, const char* name, void*) {
  try {
    auto code = source(engine(ctx), name); auto value = JS_Eval(ctx, code.data(), code.size(), name, JS_EVAL_TYPE_MODULE | JS_EVAL_FLAG_COMPILE_ONLY);
    if (JS_IsException(value)) return nullptr;
    auto* module = static_cast<JSModuleDef*>(JS_VALUE_GET_PTR(value)); auto meta = JS_GetImportMeta(ctx, module);
    JS_SetPropertyStr(ctx, meta, "url", JS_NewString(ctx, name)); JS_FreeValue(ctx, meta); JS_FreeValue(ctx, value); return module;
  } catch (const std::exception& ex) { JS_ThrowReferenceError(ctx, "%s", ex.what()); return nullptr; }
}
void evaluate(Engine* e, const std::string& code, const char* name, int flags = JS_EVAL_TYPE_GLOBAL) { checked(e->context, JS_Eval(e->context, code.data(), code.size(), name, flags)); }
void initialize(Engine* e) {
  auto config = wasm_config_new(); wasmtime_config_epoch_interruption_set(config, true);
  e->wasm = wasm_engine_new_with_config(config); e->store = wasmtime_store_new(e->wasm, nullptr, nullptr); e->active = wasmtime_store_context(e->store); wasmtime_context_set_epoch_deadline(e->active, 1);
  e->runtime = JS_NewRuntime(); JS_SetMaxStackSize(e->runtime, 4 * 1024 * 1024); e->context = JS_NewContext(e->runtime); JS_SetContextOpaque(e->context, e);
  JS_SetInterruptHandler(e->runtime, [](JSRuntime*, void* data) { return static_cast<Engine*>(data)->cancelled.load() ? 1 : 0; }, e);
  JS_SetModuleLoaderFunc(e->runtime, normalize, loadModule, e);
  JS_SetHostPromiseRejectionTracker(e->runtime, [](JSContext* ctx, JSValueConst, JSValueConst reason, bool handled, void* data) { auto* e = static_cast<Engine*>(data); if (handled) e->rejection.clear(); else e->rejection = errorText(ctx, reason); }, e);
  auto cls = e->env->GetObjectClass(e->bridge); e->sourceMethod = e->env->GetMethodID(cls, "source", "(Ljava/lang/String;)Ljava/lang/String;");
  auto method = e->env->GetMethodID(cls, "bindings", "()[Ljava/lang/String;");
  auto signatures = static_cast<jobjectArray>(e->env->CallObjectMethod(e->bridge, method)); javaError(e);
  auto object = JS_NewObject(e->context);
  for (jsize i = 0; i < e->env->GetArrayLength(signatures); ++i) {
    auto value = static_cast<jstring>(e->env->GetObjectArrayElement(signatures, i)); auto js = jsString(e, value); auto signature = text(e->context, js); JS_FreeValue(e->context, js); e->env->DeleteLocalRef(value);
    auto split = signature.find('|'); Binding binding; binding.name = signature.substr(0, split); binding.signature = signature.substr(split + 1); binding.method = e->env->GetMethodID(cls, binding.name.c_str(), binding.signature.c_str());
    size_t offset = 1; while (binding.signature[offset] != ')') {
      char kind = binding.signature[offset++];
      if (kind == 'L') { auto end = binding.signature.find(';', offset); kind = binding.signature.substr(offset, end - offset) == "java/nio/ByteBuffer" ? 'B' : 'L'; offset = end + 1; }
      binding.args.push_back(kind);
    }
    binding.result = binding.signature[offset + 1];
    JS_SetPropertyStr(e->context, object, binding.name.c_str(), JS_NewCFunctionMagic(e->context, javaCall, binding.name.c_str(), binding.args.size(), JS_CFUNC_generic_magic, e->bindings.size())); e->bindings.push_back(std::move(binding));
  }
  e->env->DeleteLocalRef(signatures); e->env->DeleteLocalRef(cls);
  auto global = JS_GetGlobalObject(e->context); JS_SetPropertyStr(e->context, global, "native", object);
  auto api = JS_NewObject(e->context); const char* names[] = {"compile","describe","instantiate","call","memory","buffer","growMemory","table","tableSize","tableGet","tableSet","tableGrow","global","globalGet","globalSet"};
  for (int i = 0; i <= GlobalSet; ++i) JS_SetPropertyStr(e->context, api, names[i], JS_NewCFunctionMagic(e->context, wasmCall, names[i], 0, JS_CFUNC_generic_magic, i));
  JS_SetPropertyStr(e->context, global, "__wasmtime", api); JS_FreeValue(e->context, global);
  evaluate(e, source(e, "wasmtime.js"), "wasmtime.js"); evaluate(e, source(e, "bootstrap.js"), "bootstrap.js");
}
void throwJava(JNIEnv* env, const std::exception& ex) { env->ThrowNew(env->FindClass("java/lang/IllegalStateException"), ex.what()); }
}

extern "C" JNIEXPORT jlong JNICALL Java_org_aquariuslang_android_NativeRuntime_create(JNIEnv* env, jclass, jobject bridge) {
  try { auto e = std::make_unique<Engine>(env, bridge); initialize(e.get()); return reinterpret_cast<jlong>(e.release()); }
  catch (const std::exception& ex) { throwJava(env, ex); return 0; }
}
extern "C" JNIEXPORT jboolean JNICALL Java_org_aquariuslang_android_NativeRuntime_step(JNIEnv* env, jclass, jlong handle, jint frames, jboolean first) {
  auto* e = reinterpret_cast<Engine*>(handle);
  try {
    if (!e) throw std::runtime_error("Runtime is closed");
    if (first) evaluate(e, "import {run} from './android-host.mjs';globalThis.finished=false;native.log('AQUARIUS_ENGINE Wasmtime 48.0.2');run(" + std::to_string(frames) + ").then(()=>{finished=true;native.log('AQUARIUS_ANDROID_OK');},e=>{finished=true;native.fail(String(e.stack??e));});", "main.mjs", JS_EVAL_TYPE_MODULE);
    evaluate(e, "pumpTimers()", "timers.js");
    for (int i = 0; i < 256 && JS_IsJobPending(e->runtime); ++i) { JSContext* ctx; if (JS_ExecutePendingJob(e->runtime, &ctx) < 0) checked(ctx, JS_EXCEPTION); }
    if (!e->rejection.empty()) throw std::runtime_error(e->rejection);
    auto global = JS_GetGlobalObject(e->context); auto finished = JS_GetPropertyStr(e->context, global, "finished"); bool done = JS_ToBool(e->context, finished); JS_FreeValue(e->context, finished); JS_FreeValue(e->context, global); return done;
  } catch (const std::exception& ex) { throwJava(env, ex); return false; }
}
extern "C" JNIEXPORT void JNICALL Java_org_aquariuslang_android_NativeRuntime_interrupt(JNIEnv*, jclass, jlong handle) {
  auto* e = reinterpret_cast<Engine*>(handle); e->cancelled = true; wasmtime_engine_increment_epoch(e->wasm);
}
extern "C" JNIEXPORT void JNICALL Java_org_aquariuslang_android_NativeRuntime_destroy(JNIEnv*, jclass, jlong handle) { delete reinterpret_cast<Engine*>(handle); }
