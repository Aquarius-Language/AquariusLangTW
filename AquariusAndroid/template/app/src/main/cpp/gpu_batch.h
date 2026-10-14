#pragma once
#include <cmath>
#include <cstdint>
#include <cstring>
#include <stdexcept>
#include <vector>

struct ByteSpan {
  const uint8_t* pointer;
  size_t length;
  const uint8_t* data() const { return pointer; }
  size_t size() const { return length; }
  bool empty() const { return length == 0; }
};
struct BatchCommand {
  uint32_t operation{}, depth{}, mode{}, vertexBytes{};
  int32_t blend{};
  uint64_t target{}, shader{}, texture{}, mesh{};
  uint32_t clip[4]{};
  float color[4]{};
  const uint8_t *uniforms{}, *user{}, *vertices{};
};
template <typename T> T batchValue(const uint8_t* p) {
  T value;
  std::memcpy(&value, p, sizeof(T));
  return value;
}
// Parse and bound the complete packet before touching GPU resources. Both
// supported Android ABIs are little-endian. Pointers live only for this call.
inline std::vector<BatchCommand> parseBatch(ByteSpan bytes) {
  if (bytes.size() < 8 || bytes.size() > 64 * 1024 * 1024 ||
      batchValue<uint32_t>(bytes.data()) != 0x31475141)
    throw std::runtime_error("Invalid GPU batch header");
  uint32_t count = batchValue<uint32_t>(bytes.data() + 4);
  if (count > 65536) throw std::runtime_error("GPU batch command limit exceeded");
  std::vector<BatchCommand> commands;
  commands.reserve(count);
  size_t offset = 8;
  for (uint32_t i = 0; i < count; i++) {
    if (bytes.size() - offset < 16) throw std::runtime_error("Truncated GPU batch");
    const uint8_t* p = bytes.data() + offset;
    uint32_t length = batchValue<uint32_t>(p + 4);
    if (length < 16 || length > bytes.size() - offset)
      throw std::runtime_error("Invalid GPU command length");
    BatchCommand c;
    c.operation = batchValue<uint32_t>(p);
    c.target = batchValue<uint64_t>(p + 8);
    if (c.operation == 1 && length == 32) {
      for (int j = 0; j < 4; j++) {
        c.color[j] = batchValue<float>(p + 16 + j * 4);
        if (!std::isfinite(c.color[j]) || c.color[j] < 0 || c.color[j] > 1)
          throw std::runtime_error("Invalid GPU clear color");
      }
    } else if ((c.operation == 2 && length >= 2208) || (c.operation == 3 && length == 2216)) {
      c.shader = batchValue<uint64_t>(p + 16);
      c.texture = batchValue<uint64_t>(p + 24);
      c.depth = batchValue<uint32_t>(p + 32);
      c.blend = batchValue<int32_t>(p + 36);
      c.mode = batchValue<uint32_t>(p + 40);
      for (int j = 0; j < 4; j++) c.clip[j] = batchValue<uint32_t>(p + 44 + j * 4);
      c.vertexBytes = batchValue<uint32_t>(p + 60);
      if ((c.operation == 2 && (!c.vertexBytes || c.vertexBytes % 48 || c.vertexBytes != length - 2208)) ||
          (c.operation == 3 && c.vertexBytes != 0) || c.depth > 1 || c.blend < 0 || c.blend > 4 ||
          (c.mode != 0 && c.mode != 1 && c.mode != 4))
        throw std::runtime_error("Invalid GPU draw record");
      c.uniforms = p + 64; c.user = p + 1184; c.vertices = p + 2208;
      if (c.operation == 3) c.mesh = batchValue<uint64_t>(p + 2208);
    } else throw std::runtime_error("Unknown GPU batch command");
    commands.push_back(c);
    offset += length;
  }
  if (offset != bytes.size()) throw std::runtime_error("Trailing GPU batch bytes");
  return commands;
}
