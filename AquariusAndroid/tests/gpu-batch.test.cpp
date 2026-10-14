#include "../template/app/src/main/cpp/gpu_batch.h"
#include <cassert>
#include <iostream>

template<class T> void put(std::vector<uint8_t>& bytes, size_t offset, T value) {
  std::memcpy(bytes.data() + offset, &value, sizeof(value));
}
bool rejects(const std::vector<uint8_t>& bytes) {
  try { parseBatch({bytes.data(),bytes.size()}); return false; }
  catch (const std::runtime_error&) { return true; }
}
int main() {
  std::vector<uint8_t> bytes(8 + 32 + 2256);
  put<uint32_t>(bytes,0,0x31475141); put<uint32_t>(bytes,4,2);
  put<uint32_t>(bytes,8,1); put<uint32_t>(bytes,12,32); put<uint64_t>(bytes,16,4294967303);
  put<float>(bytes,36,1);
  size_t p=40;
  put<uint32_t>(bytes,p,2); put<uint32_t>(bytes,p+4,2256); put<uint64_t>(bytes,p+8,4294967303);
  put<uint64_t>(bytes,p+16,2); put<uint64_t>(bytes,p+24,3); put<uint32_t>(bytes,p+40,4); put<uint32_t>(bytes,p+60,48);
  auto records=parseBatch({bytes.data(),bytes.size()});
  assert(records.size()==2 && records[0].target==4294967303 && records[1].vertexBytes==48);
  // Truncated, malformed and oversized metadata must never reach GPU recording.
  auto bad=bytes; bad.pop_back(); assert(rejects(bad));
  bad=bytes; put<uint32_t>(bad,4,65537); assert(rejects(bad));
  bad=bytes; put<uint32_t>(bad,p+4,0xffffffff); assert(rejects(bad));
  bad=bytes; put<uint32_t>(bad,p+60,96); assert(rejects(bad));
  bad=bytes; put<uint32_t>(bad,p+40,99); assert(rejects(bad));
  bad=bytes; put<float>(bad,24,std::nanf("")); assert(rejects(bad));
  bad=bytes; bad.push_back(0); assert(rejects(bad));
  // A direct buffer may contain a subview that starts at an unaligned byte.
  bad.insert(bad.begin(),0); auto valid=parseBatch({bad.data()+1,bytes.size()}); assert(valid.size()==2);
  bad=bytes; bad.resize(40+2216); put<uint32_t>(bad,40,3); put<uint32_t>(bad,44,2216);
  put<uint32_t>(bad,100,0); put<uint64_t>(bad,2248,4294967310);
  auto meshes=parseBatch({bad.data(),bad.size()}); assert(meshes[1].mesh==4294967310);
  std::cout << "ANDROID_GPU_BATCH_PARSER_OK\n";
}
