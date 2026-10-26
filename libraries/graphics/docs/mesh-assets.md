# Binary retained meshes and asset startup

`Processing.loadMesh(path)` / `載入網格(path)` and the corresponding canvas method load an AQM1 asset as a normal editable retained triangle shape. This bypasses JSON parsing and individual vertex/normal calls. OBJ parsing and `PBR.CreateMesh` remain available for dynamic geometry.

AQM1 has a 16-byte little-endian header: four ASCII bytes `AQM1`, uint32 vertex count, uint32 component count (8), uint32 reserved (0). Each vertex contains eight little-endian float32 values: position xyz, normal xyz, UV xy. Vertex count must be nonzero, divisible by three, and at most one million. All components must be finite and normals nonzero. Vertex order is retained, including vertex-indexed tangent data. The shape starts white with no stroke.

Web exports keep assets as files in `assets/` beside `program.wasm`, which retains their paths and lengths (marked external) without the `aquarius.assets` bytes; Android APKs store them uncompressed under `assets/aquarius/`. The original standalone Wasm stays self contained.

Texture assets may be gzip-compressed. The graphics library inflates them with its own bounded decoder (`src/common/codecs.h`) on every target, checking gzip corruption and the exact output size before upload. Float16 texture components are checked for finiteness.
