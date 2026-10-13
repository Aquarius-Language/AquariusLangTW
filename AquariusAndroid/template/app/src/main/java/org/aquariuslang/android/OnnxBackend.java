package org.aquariuslang.android;

import ai.onnxruntime.*;
import java.nio.*;
import java.util.*;
import org.json.*;

/** ONNX host adapter. Tensor shape/type validation remains in the shared core registrar. */
final class OnnxBackend implements AutoCloseable {
  private final NativeBridge bridge;
  private final OrtEnvironment environment = OrtEnvironment.getEnvironment();
  private final Map<Integer, OrtSession> sessions = new HashMap<>();
  private int next;

  OnnxBackend(NativeBridge bridge) {
    this.bridge = bridge;
  }

  JSONObject call(String op, JSONObject args) {
    try {
      if (op.equals("create")) {
        if (sessions.size() >= 8) throw new IllegalArgumentException("At most eight ONNX sessions");
        try (OrtSession.SessionOptions options = new OrtSession.SessionOptions()) {
          options.setIntraOpNumThreads(2);
          OrtSession session =
              environment.createSession(
                  args.has("asset")
                      ? bridge.assetBytes(args.getString("asset"), 256 * 1024 * 1024)
                      : NativeBridge.unbase64(args.getString("model")),
                  options);
          int id = ++next;
          sessions.put(id, session);
          return NativeBridge.object(
              "id",
              id,
              "inputs",
              metadata(session.getInputInfo()),
              "outputs",
              metadata(session.getOutputInfo()));
        }
      }
      int id = args.getInt("id");
      OrtSession session = sessions.get(id);
      if (op.equals("dispose")) {
        if (session != null) {
          sessions.remove(id);
          session.close();
        }
        return new JSONObject();
      }
      if (session == null) throw new IllegalStateException("Closed: ONNX session closed");
      if (op.equals("run")) {
        JSONObject feeds = args.getJSONObject("inputs");
        Map<String, OnnxTensor> tensors = new HashMap<>();
        try {
          for (Iterator<String> inputNames = feeds.keys(); inputNames.hasNext(); ) {
            String name = inputNames.next();
            JSONObject value = feeds.getJSONObject(name);
            JSONArray shape = value.getJSONArray("shape");
            long[] dims = new long[shape.length()];
            for (int i = 0; i < dims.length; i++) dims[i] = shape.getLong(i);
            ByteBuffer data =
                ByteBuffer.wrap(NativeBridge.unbase64(value.getString("data")))
                    .order(ByteOrder.LITTLE_ENDIAN);
            String type = value.getString("type");
            OnnxTensor tensor =
                switch (type) {
                  case "float32" ->
                      OnnxTensor.createTensor(environment, data.asFloatBuffer(), dims);
                  case "float64" ->
                      OnnxTensor.createTensor(environment, data.asDoubleBuffer(), dims);
                  case "int32" -> OnnxTensor.createTensor(environment, data.asIntBuffer(), dims);
                  case "uint8" ->
                      OnnxTensor.createTensor(environment, data, dims, OnnxJavaType.UINT8);
                  case "bool" ->
                      OnnxTensor.createTensor(environment, data, dims, OnnxJavaType.BOOL);
                  default -> throw new IllegalArgumentException("Unsupported tensor type");
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
              byte[] bytes;
              switch (info.type) {
                case FLOAT:
                  {
                    FloatBuffer values = tensor.getFloatBuffer();
                    ByteBuffer b =
                        ByteBuffer.allocate(values.remaining() * 4).order(ByteOrder.LITTLE_ENDIAN);
                    while (values.hasRemaining()) b.putFloat(values.get());
                    bytes = b.array();
                    break;
                  }
                case DOUBLE:
                  {
                    DoubleBuffer values = tensor.getDoubleBuffer();
                    ByteBuffer b =
                        ByteBuffer.allocate(values.remaining() * 8).order(ByteOrder.LITTLE_ENDIAN);
                    while (values.hasRemaining()) b.putDouble(values.get());
                    bytes = b.array();
                    break;
                  }
                case INT32:
                  {
                    IntBuffer values = tensor.getIntBuffer();
                    ByteBuffer b =
                        ByteBuffer.allocate(values.remaining() * 4).order(ByteOrder.LITTLE_ENDIAN);
                    while (values.hasRemaining()) b.putInt(values.get());
                    bytes = b.array();
                    break;
                  }
                default:
                  {
                    ByteBuffer values = tensor.getByteBuffer();
                    bytes = new byte[values.remaining()];
                    values.get(bytes);
                  }
              }
              JSONArray dims = new JSONArray();
              for (long n : info.getShape()) dims.put(n);
              out.put(
                  name,
                  NativeBridge.object(
                      "type", type(info.type), "shape", dims, "data", NativeBridge.base64(bytes)));
            }
            return out;
          }
        } finally {
          for (OnnxTensor t : tensors.values()) t.close();
        }
      }
      throw new IllegalArgumentException("Unknown inference operation");
    } catch (Exception e) {
      throw new IllegalStateException(e.getMessage(), e);
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
