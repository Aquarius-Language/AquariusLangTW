package org.aquariuslang.android;

import com.caoccao.javet.interop.V8Host;
import com.caoccao.javet.interop.V8Runtime;
import com.caoccao.javet.interop.options.V8RuntimeOptions;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Consumer;

/** Engine adapter only. Aquarius control flow and dynamic values remain in program.wasm. */
public final class NativeRuntime implements AutoCloseable {
  private final NativeBridge bridge;
  private final Consumer<String> failure;
  private final AtomicBoolean started = new AtomicBoolean();
  private volatile V8Runtime engine;
  private volatile boolean closed;
  private volatile Thread actor;

  public NativeRuntime(NativeBridge bridge, Consumer<String> failure) {
    this.bridge = bridge;
    this.failure = failure;
  }

  public void start(int frames) {
    if (!started.compareAndSet(false, true)) return;
    actor =
        new Thread(
            () -> {
              try {
                var icu = new java.io.File(bridge.activity.getFilesDir(), "icudtl.dat");
                try (var input = bridge.activity.getAssets().open("aquarius/icudtl.dat")) {
                  java.nio.file.Files.copy(
                      input, icu.toPath(), java.nio.file.StandardCopyOption.REPLACE_EXISTING);
                }
                V8RuntimeOptions.V8_FLAGS.setIcuDataFile(icu.getAbsolutePath());
              } catch (Exception e) {
                if (!closed) failure.accept("Unicode runtime initialization failed: " + e);
                bridge.releaseRuntime();
                return;
              }
              if (closed) {
                bridge.releaseRuntime();
                return;
              }
              try (V8Runtime runtime = V8Host.getV8I18nInstance().createV8Runtime()) {
                engine = runtime;
                try (var object = runtime.createV8ValueObject()) {
                  object.bind(bridge);
                  runtime.getGlobalObject().set("native", object);
                }
                // Resolve by canonical deployment name. Import spellings such as
                // ./values.mjs must share one module and therefore one symbol ABI.
                var modules = new RuntimeModules<com.caoccao.javet.values.reference.V8Module>();
                runtime.setV8ModuleResolver(
                    (r, name, referrer) ->
                        modules.resolve(
                            name,
                            path ->
                                r.getExecutor(bridge.source(path))
                                    .setResourceName(path)
                                    .compileV8Module()));
                bridge.moduleImporter = name -> {
                  var module = modules.resolve(name, path -> runtime.getExecutor(bridge.source(path)).setResourceName(path).compileV8Module());
                  if (module.getStatus() == 0 && !module.instantiate())
                    throw new IllegalStateException("Could not instantiate library: " + name);
                  try (var evaluated = module.evaluate(true);
                       var namespace = module.getNamespace();
                       var result = runtime.createV8ValueObject()) {
                    result.set("ready", evaluated);
                    result.set("namespace", namespace);
                    runtime.getGlobalObject().set("__aquaImportedLibrary", result);
                  }
                };
                runtime.getExecutor(bridge.source("bootstrap.js")).executeVoid();
                runtime.getGlobalObject().set("frameLimit", frames);
                runtime
                    .getExecutor(
                        "import {run} from"
                            + " './android-host.mjs';globalThis.finished=false;run(frameLimit).then(()=>{finished=true;native.log('AQUARIUS_ANDROID_OK');},e=>{finished=true;native.fail(String(e.stack??e));});")
                    .setModule(true)
                    .setResourceName("main.mjs")
                    .executeVoid();
                while (!closed && !runtime.getExecutor("finished").executeBoolean()) {
                  runtime.getExecutor("pumpTimers()").executeVoid();
                  runtime.await();
                  Thread.sleep(1);
                }
              } catch (Throwable e) {
                if (!closed) {
                  bridge.log("AQUARIUS_ANDROID_ERROR: " + e);
                  failure.accept("應用程式無法執行。\n" + e.getMessage());
                }
              } finally {
                bridge.moduleImporter = null;
                engine = null;
                bridge.releaseRuntime();
              }
            },
            "Aquarius-Wasm");
    actor.start();
  }

  public void close() {
    closed = true;
    V8Runtime runtime = engine;
    if (runtime != null) runtime.terminateExecution();
    Thread thread = actor;
    if (thread != null) thread.interrupt();
  }
}
