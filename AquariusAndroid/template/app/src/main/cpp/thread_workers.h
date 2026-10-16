// Isolated Wasmtime stores on native OS threads. No QuickJS/JNI calls on workers.
#include <thread>
#include <mutex>
#include <condition_variable>
#include <unordered_map>
#include <chrono>
#include <limits>

class AquariusThread {
  std::thread actor;
  std::mutex mutex;
  std::condition_variable wake;
  std::atomic<bool> cancelled{false};
  static void check(wasmtime_error_t* error, wasm_trap_t* trap = nullptr) {
    if (!error && !trap) return;
    wasm_byte_vec_t message;
    if (error) wasmtime_error_message(error, &message); else wasm_trap_message(trap, &message);
    std::string text(message.data, message.size); wasm_byte_vec_delete(&message);
    if (error) wasmtime_error_delete(error); if (trap) wasm_trap_delete(trap);
    throw std::runtime_error(text);
  }
  static wasm_trap_t* service(void* data, wasmtime_caller_t* caller, const wasmtime_val_t* a, size_t, wasmtime_val_t* result, size_t) {
    auto* self = static_cast<AquariusThread*>(data);
    try {
      auto* context = wasmtime_caller_context(caller); wasmtime_extern_t memory;
      if (!wasmtime_caller_export_get(caller, "memory", 6, &memory)) throw std::runtime_error("Missing worker memory");
      auto* bytes = wasmtime_memory_data(context, &memory.of.memory); auto size = wasmtime_memory_data_size(context, &memory.of.memory);
      auto range = [&](uint32_t p, size_t n) { if (p > size || n > size - p) throw std::runtime_error("Invalid worker value"); return bytes + p; };
      auto read = [&](uint32_t p) { uint32_t n; memcpy(&n, range(p, 4), 4); return n; };
      auto raw = [&](uint32_t tag, uint32_t ref, double number) { auto* out = range(a[6].of.i32, 16); memcpy(out, &tag, 4); memcpy(out + 4, &ref, 4); memcpy(out + 8, &number, 8); };
      if (a[0].of.i32 == 0) {
        uint32_t p = a[3].of.i32, n = read(p); auto* chars = range(p + 4, static_cast<size_t>(n) * 2);
        std::u16string name(n, 0); memcpy(name.data(), chars, n * 2);
        uint32_t id = name == u"len" || name == u"長度" ? 1 : name == u"last" || name == u"最後一個" ? 2 : name == u"rest" || name == u"其餘" ? 3 : name == u"push" || name == u"加入" ? 4 : name == u"floodFill" || name == u"區域填色" ? 5 : 0;
        if (!id) throw std::runtime_error("Worker cannot access host capability"); raw(14, id, 0);
      } else if (a[0].of.i32 == 4 && a[2].of.i32 == 107) raw(4, 0, self->cancelled ? 1 : 0);
      else if (a[0].of.i32 == 4 && a[2].of.i32 == 106) {
        double ms; memcpy(&ms, range(a[4].of.i32 + 8, 8), 8);
        std::unique_lock<std::mutex> lock(self->mutex);
        self->wake.wait_for(lock, std::chrono::milliseconds(static_cast<int>(ms)), [&] { return self->cancelled.load(); }); raw(4, 0, self->cancelled ? 0 : 1);
      } else throw std::runtime_error("Worker cannot access host handles or start/wait for other threads.");
      result[0].kind = WASMTIME_I32; result[0].of.i32 = 0; return nullptr;
    } catch (const std::exception& error) { return wasmtime_trap_new(error.what(), strlen(error.what())); }
  }
public:
  std::atomic<bool> done{false};
  std::vector<uint8_t> packet;
  std::string error;
  AquariusThread(wasm_engine_t* engine, wasmtime_module_t* module, std::vector<uint8_t> snapshot, int closure, int args, int count) {
    actor = std::thread([this, engine, module, snapshot = std::move(snapshot), closure, args, count]() mutable {
      try {
        std::unique_ptr<wasmtime_store_t, decltype(&wasmtime_store_delete)> store(wasmtime_store_new(engine, nullptr, nullptr), wasmtime_store_delete);
        auto* context = wasmtime_store_context(store.get()); wasmtime_context_set_epoch_deadline(context, std::numeric_limits<uint64_t>::max() / 2);
        wasm_valtype_vec_t params, results; wasm_valtype_vec_new_uninitialized(&params, 7); wasm_valtype_vec_new_uninitialized(&results, 1);
        for (size_t i = 0; i < 7; ++i) params.data[i] = wasm_valtype_new_i32(); results.data[0] = wasm_valtype_new_i32();
        auto* type = wasm_functype_new(&params, &results); wasmtime_extern_t host{}; host.kind = WASMTIME_EXTERN_FUNC;
        wasmtime_func_new(context, type, service, this, nullptr, &host.of.func); wasm_functype_delete(type);
        wasmtime_instance_t instance; wasm_trap_t* trap = nullptr;
        auto* instantiateError = wasmtime_instance_new(context, module, &host, 1, &instance, &trap); check(instantiateError, trap);
        auto call = [&](const char* name, std::initializer_list<int> values) {
          wasmtime_extern_t function; if (!wasmtime_instance_export_get(context, &instance, name, strlen(name), &function)) throw std::runtime_error("Missing thread ABI export");
          std::vector<wasmtime_val_t> input; for (int v : values) { wasmtime_val_t arg{}; arg.kind = WASMTIME_I32; arg.of.i32 = v; input.push_back(arg); }
          wasmtime_val_t output; wasm_trap_t* t = nullptr; auto* e = wasmtime_func_call(context, &function.of.func, input.data(), input.size(), &output, 1, &t); check(e, t); return output.of.i32;
        };
        call("aqua_thread_reserve", {static_cast<int>(snapshot.size())}); wasmtime_extern_t memory;
        if (!wasmtime_instance_export_get(context, &instance, "memory", 6, &memory)) throw std::runtime_error("Missing thread memory");
        memcpy(wasmtime_memory_data(context, &memory.of.memory), snapshot.data(), snapshot.size()); snapshot.clear(); snapshot.shrink_to_fit();
        int execution = call("aqua_thread_enter", {closure, args, count});
        for (;;) {
          if (cancelled) throw std::runtime_error("Thread: Execution cancelled.");
          int status = call("aqua_run", {execution, 32768}); if (status == 1) continue;
          if (status != 3 && status != 4) throw std::runtime_error("Invalid worker execution status");
          int cell = call("aqua_result", {execution}), encoded = call("aqua_thread_encode", {cell});
          if (!encoded) throw std::runtime_error("Thread results must be portable data (depth <= 128, graph <= 4096, packet <= 16 MiB).");
          auto* bytes = wasmtime_memory_data(context, &memory.of.memory); uint32_t size; memcpy(&size, bytes + encoded, 4);
          packet.assign(bytes + encoded, bytes + encoded + size + 4); break;
        }
      } catch (const std::exception& failure) { error = failure.what(); }
      done.store(true, std::memory_order_release);
    });
  }
  void cancel() { cancelled = true; wake.notify_all(); }
  ~AquariusThread() { cancel(); if (actor.joinable()) actor.join(); }
};
