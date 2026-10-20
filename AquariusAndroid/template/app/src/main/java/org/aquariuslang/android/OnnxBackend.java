package org.aquariuslang.android;

import ai.onnxruntime.*;
import ai.onnxruntime.providers.NNAPIFlags;
import android.os.Build;
import android.util.Base64;
import java.nio.*;
import java.util.*;
import org.json.*;

/**
 * ONNX contract version 2 host adapter for Android. Selection policy (Auto/GPU/NPU, ranking, fallback) and tensor
 * validation live in the shared core (onnx-providers.mjs / onnx-registration.mjs); this class only reports ranked,
 * probed execution devices and opens sessions for an exact provider plan.
 *
 * <p>Ranking: WebGPU (Vulkan GPU through Dawn) → NNAPI (vendor NPU/DSP drivers) → XNNPACK (optimized ARM CPU) → CPU.
 * Each provider is verified once per process by building and running a tiny convolution model, so a missing driver
 * is reported before an application model is opened.
 */
final class OnnxBackend implements AutoCloseable {
  /** Same bytes as AquariusCore/onnx/OnnxProbeModel.cs: Y = Conv(X, W=2) over float32[1,1,1,1]. */
  private static final String PROBE_MODEL =
      "CAgSDmFxdWFyaXVzLXByb2JlOmcKDwoBWAoBVxIBWSIEQ29udhIFcHJvYmUqEwgBCAEIAQgBEAFCAVdKBAAAAEBaGwoBWBIWChQIARIQCgIIAQoCCAEKAggBCgIIAWIbCgFZEhYKFAgBEhAKAggBCgIIAQoCCAEKAggBQgQKABAN";
  private static final int MAX_SESSIONS = 8;
  private static final int THREADS =
      Math.max(1, Math.min(4, Runtime.getRuntime().availableProcessors() / 2));
  private static final boolean EMULATOR =
      Build.HARDWARE.contains("ranchu")
          || Build.HARDWARE.contains("goldfish")
          || Build.PRODUCT.contains("sdk")
          || Build.FINGERPRINT.startsWith("generic");

  /** Strategy for one execution provider on one hardware class. */
  private abstract static class Provider {
    final String id, provider, kind, name;
    final boolean automatic;
    final OrtProvider engine;

    Provider(String provider, String kind, String name, boolean automatic, OrtProvider engine) {
      this.id = provider;
      this.provider = provider;
      this.kind = kind;
      this.name = name;
      this.automatic = automatic;
      this.engine = engine;
    }

    /** Session-level tuning required or recommended by this provider. */
    void configure(OrtSession.SessionOptions options) throws OrtException {
      // Accelerated sessions keep CPU fallback nodes on one thread, leaving cores for camera and UI.
      if (!kind.equals("CPU")) options.setIntraOpNumThreads(1);
    }

    abstract void append(OrtSession.SessionOptions options) throws OrtException;
  }

  private static final List<Provider> PROVIDERS =
      List.of(
          new Provider("WebGPU", "GPU", "Vulkan GPU (WebGPU/Dawn)", !EMULATOR, OrtProvider.WEBGPU) {
            @Override
            void append(OrtSession.SessionOptions options) throws OrtException {
              options.addWebGPU(Map.of());
            }
          },
          new Provider("NNAPI", "NPU", "Android NNAPI accelerators", !EMULATOR, OrtProvider.NNAPI) {
            @Override
            void append(OrtSession.SessionOptions options) throws OrtException {
              // Accelerators only: nodes NNAPI would run on its reference CPU go to ORT's faster CPU kernels.
              options.addNnapi(EnumSet.of(NNAPIFlags.CPU_DISABLED));
            }
          },
          // ORT 1.30's XNNPACK optimizer can abort natively on MediaPipe graphs on
          // x86_64 emulators, after its small convolution probe succeeds. Native
          // aborts cannot participate in the managed Auto fallback policy.
          new Provider("XNNPACK", "CPU", "XNNPACK (" + THREADS + " threads)", !EMULATOR, OrtProvider.XNNPACK) {
            @Override
            void configure(OrtSession.SessionOptions options) throws OrtException {
              // ORT guidance: XNNPACK owns the thread pool; ORT's intra-op pool stays at one thread.
              options.setIntraOpNumThreads(1);
            }

            @Override
            void append(OrtSession.SessionOptions options) throws OrtException {
              options.addXnnpack(Map.of("intra_op_num_threads", Integer.toString(THREADS)));
            }
          },
          new Provider("CPU", "CPU", "ONNX Runtime CPU (" + THREADS + " threads)", true, OrtProvider.CPU) {
            @Override
            void configure(OrtSession.SessionOptions options) throws OrtException {
              options.setIntraOpNumThreads(THREADS);
            }

            @Override
            void append(OrtSession.SessionOptions options) {}
          });

  private final NativeBridge bridge;
  private final OrtEnvironment environment = OrtEnvironment.getEnvironment();
  private final Map<Integer, OrtSession> sessions = new HashMap<>();
  private JSONArray devices;
  private int next;

  OnnxBackend(NativeBridge bridge) {
    this.bridge = bridge;
  }

  JSONObject call(String op, JSONObject args) {
    try {
      switch (op) {
        case "devices":
          return NativeBridge.object("devices", devices());
        case "create":
          return create(args);
        case "dispose":
          {
            OrtSession session = sessions.remove(args.getInt("id"));
            if (session != null) session.close();
            return new JSONObject();
          }
        case "run":
          {
            OrtSession session = sessions.get(args.getInt("id"));
            if (session == null) throw new IllegalStateException("Closed: ONNX session closed");
            return run(session, args);
          }
        default:
          throw new IllegalArgumentException("InvalidArgument: Unknown inference operation");
      }
    } catch (IllegalStateException | IllegalArgumentException e) {
      throw e;
    } catch (Exception e) {
      throw new IllegalStateException("Engine: " + e.getMessage(), e);
    }
  }

  /** Ranked devices with probe results, computed once per process. */
  private synchronized JSONArray devices() throws JSONException {
    if (devices != null) return devices;
    EnumSet<OrtProvider> built = OrtEnvironment.getAvailableProviders();
    JSONArray list = new JSONArray();
    for (Provider p : PROVIDERS) {
      String reason = null;
      if (!built.contains(p.engine)) reason = "此 ONNX Runtime 建置不含 " + p.provider + "。";
      else if (!p.provider.equals("CPU")) reason = probe(p);
      boolean available = reason == null;
      if (available && !p.automatic && EMULATOR) reason = "Android 模擬器上僅在明確指定時使用。";
      list.put(
          NativeBridge.object(
              "id", p.id, "provider", p.provider, "kind", p.kind, "name", p.name,
              "available", available, "automatic", available && p.automatic,
              "reason", reason == null ? JSONObject.NULL : reason));
    }
    devices = list;
    return devices;
  }

  /** Builds and runs the probe model through one provider; returns null on success or the failure reason. */
  private String probe(Provider provider) {
    try (OrtSession.SessionOptions options = options(List.of(provider));
        OrtSession session =
            environment.createSession(Base64.decode(PROBE_MODEL, Base64.DEFAULT), options);
        OnnxTensor input =
            OnnxTensor.createTensor(environment, FloatBuffer.wrap(new float[] {1.5f}), new long[] {1, 1, 1, 1});
        OrtSession.Result result = session.run(Map.of("X", input))) {
      float value = ((OnnxTensor) result.get(0)).getFloatBuffer().get(0);
      return Math.abs(value - 3f) < 1e-3f ? null : "探測模型結果錯誤（" + value + "）。";
    } catch (Throwable e) {
      String message = String.valueOf(e.getMessage());
      return message.length() > 300 ? message.substring(0, 300) + "…" : message;
    }
  }

  private static Provider provider(String id) {
    for (Provider p : PROVIDERS) if (p.id.equals(id)) return p;
    throw new IllegalArgumentException("InvalidArgument: Unknown ONNX device " + id);
  }

  /** Shared tuning: full graph optimization, no idle spinning, then each provider in plan order. */
  private static OrtSession.SessionOptions options(List<Provider> plan) throws OrtException {
    OrtSession.SessionOptions options = new OrtSession.SessionOptions();
    try {
      options.setOptimizationLevel(OrtSession.SessionOptions.OptLevel.ALL_OPT);
      options.setExecutionMode(OrtSession.SessionOptions.ExecutionMode.SEQUENTIAL);
      options.addConfigEntry("session.intra_op.allow_spinning", "0");
      options.addConfigEntry("session.inter_op.allow_spinning", "0");
      for (Provider p : plan) {
        p.configure(options);
        p.append(options);
      }
      return options;
    } catch (OrtException | RuntimeException e) {
      options.close();
      throw e;
    }
  }

  private JSONObject create(JSONObject args) throws Exception {
    if (sessions.size() >= MAX_SESSIONS)
      throw new IllegalArgumentException("InvalidArgument: At most eight ONNX sessions");
    JSONArray ids = args.optJSONArray("plan");
    List<Provider> plan = new ArrayList<>();
    if (ids == null || ids.length() == 0) plan.add(provider("CPU"));
    else for (int i = 0; i < ids.length(); i++) plan.add(provider(ids.getString(i)));
    byte[] model =
        args.has("asset")
            ? bridge.assetBytes(args.getString("asset"), 256 * 1024 * 1024)
            : NativeBridge.unbase64(args.getString("model"));
    OrtSession session;
    try (OrtSession.SessionOptions options = options(plan)) {
      session = environment.createSession(model, options);
    } catch (OrtException e) {
      throw new IllegalStateException(
          "Engine: " + plan.get(0).provider + " initialization failed: " + e.getMessage(), e);
    }
    int id = ++next;
    sessions.put(id, session);
    JSONArray providers = new JSONArray();
    for (Provider p : plan) providers.put(p.provider);
    if (!plan.contains(provider("CPU"))) providers.put("CPU");
    return NativeBridge.object(
        "id", id,
        "inputs", metadata(session.getInputInfo()),
        "outputs", metadata(session.getOutputInfo()),
        "providers", providers);
  }

  private JSONObject run(OrtSession session, JSONObject args) throws Exception {
    JSONObject feeds = args.getJSONObject("inputs");
    Map<String, OnnxTensor> tensors = new HashMap<>();
    try {
      for (Iterator<String> inputNames = feeds.keys(); inputNames.hasNext(); ) {
        String name = inputNames.next();
        JSONObject value = feeds.getJSONObject(name);
        JSONArray shape = value.getJSONArray("shape");
        long[] dims = new long[shape.length()];
        for (int i = 0; i < dims.length; i++) dims[i] = shape.getLong(i);
        byte[] bytes = NativeBridge.unbase64(value.getString("data"));
        // Direct native-order buffers are bound by ORT in place (no second JNI copy). Transport bytes are
        // little-endian, which is the native order of every Android ABI this template builds.
        ByteBuffer data = ByteBuffer.allocateDirect(bytes.length).order(ByteOrder.nativeOrder());
        data.put(bytes).flip();
        String type = value.getString("type");
        OnnxTensor tensor =
            switch (type) {
              case "float32" -> OnnxTensor.createTensor(environment, data.asFloatBuffer(), dims);
              case "float64" -> OnnxTensor.createTensor(environment, data.asDoubleBuffer(), dims);
              case "int32" -> OnnxTensor.createTensor(environment, data.asIntBuffer(), dims);
              case "uint8" -> OnnxTensor.createTensor(environment, data, dims, OnnxJavaType.UINT8);
              case "bool" -> OnnxTensor.createTensor(environment, data, dims, OnnxJavaType.BOOL);
              default -> throw new IllegalArgumentException("Unsupported: tensor type " + type);
            };
        tensors.put(name, tensor);
      }
      JSONArray outputs = args.getJSONArray("outputs");
      Set<String> names = new LinkedHashSet<>();
      for (int i = 0; i < outputs.length(); i++) names.add(outputs.getString(i));
      try (OrtSession.Result result = session.run(tensors, names)) {
        JSONObject out = new JSONObject();
        for (String name : names) {
          OnnxTensor tensor = (OnnxTensor) result.get(name).orElseThrow();
          TensorInfo info = tensor.getInfo();
          JSONArray dims = new JSONArray();
          for (long n : info.getShape()) dims.put(n);
          out.put(
              name,
              NativeBridge.object(
                  "type", type(info.type), "shape", dims, "data", NativeBridge.base64(bytes(tensor, info))));
        }
        return out;
      }
    } finally {
      for (OnnxTensor t : tensors.values()) t.close();
    }
  }

  /** Bulk little-endian serialization of an output tensor (no per-element loop). */
  private static byte[] bytes(OnnxTensor tensor, TensorInfo info) {
    switch (info.type) {
      case FLOAT:
        {
          FloatBuffer values = tensor.getFloatBuffer();
          ByteBuffer b = ByteBuffer.allocate(values.remaining() * 4).order(ByteOrder.LITTLE_ENDIAN);
          b.asFloatBuffer().put(values);
          return b.array();
        }
      case DOUBLE:
        {
          DoubleBuffer values = tensor.getDoubleBuffer();
          ByteBuffer b = ByteBuffer.allocate(values.remaining() * 8).order(ByteOrder.LITTLE_ENDIAN);
          b.asDoubleBuffer().put(values);
          return b.array();
        }
      case INT32:
        {
          IntBuffer values = tensor.getIntBuffer();
          ByteBuffer b = ByteBuffer.allocate(values.remaining() * 4).order(ByteOrder.LITTLE_ENDIAN);
          b.asIntBuffer().put(values);
          return b.array();
        }
      case UINT8:
      case BOOL:
        {
          ByteBuffer values = tensor.getByteBuffer();
          byte[] bytes = new byte[values.remaining()];
          values.get(bytes);
          return bytes;
        }
      default:
        throw new IllegalArgumentException("Unsupported: output tensor type " + info.type);
    }
  }

  private static String type(OnnxJavaType type) {
    return switch (type) {
      case FLOAT -> "float32";
      case DOUBLE -> "float64";
      case INT32 -> "int32";
      case UINT8 -> "uint8";
      case BOOL -> "bool";
      default -> "unsupported";
    };
  }

  private static JSONArray metadata(Map<String, NodeInfo> nodes) {
    JSONArray list = new JSONArray();
    for (var entry : nodes.entrySet()) {
      ValueInfo value = entry.getValue().getInfo();
      if (value instanceof TensorInfo tensor) {
        JSONArray dims = new JSONArray();
        for (long n : tensor.getShape()) dims.put(n);
        list.put(
            NativeBridge.object("name", entry.getKey(), "type", type(tensor.type), "shape", dims));
      } else
        list.put(
            NativeBridge.object(
                "name", entry.getKey(), "type", "unsupported", "shape", new JSONArray()));
    }
    return list;
  }

  public void close() {
    for (OrtSession s : sessions.values())
      try {
        s.close();
      } catch (Exception ignored) {
      }
    sessions.clear();
  }
}
