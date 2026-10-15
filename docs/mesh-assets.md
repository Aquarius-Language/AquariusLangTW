# Binary retained meshes and asset startup

`Processing.loadMesh(path)` / `載入網格(path)` and the corresponding canvas method load an AQM1 asset as a normal editable retained triangle shape. This bypasses JSON parsing and individual vertex/normal calls. OBJ parsing and `PBR.CreateMesh` remain available for dynamic geometry.

AQM1 has a 16-byte little-endian header: four ASCII bytes `AQM1`, uint32 vertex count, uint32 component count (8), uint32 reserved (0). Each vertex contains eight little-endian float32 values: position xyz, normal xyz, UV xy. Vertex count must be nonzero, divisible by three, and at most one million. All components must be finite and normals nonzero. Vertex order is retained, including vertex-indexed tangent data. The shape starts white with no stroke.

Web exports place each asset in a deterministic flat `asset-<SHA256 of package path>.bin` file. `program.json` contains filenames with `assetEncoding: "files"`; `program.wasm` retains asset paths without duplicating bytes. Startup loads binary assets with four fetch workers while compiling Wasm. Inline base64 bundles remain supported. The original standalone Wasm stays self contained.

Texture loading uses bounded native `DecompressionStream` in browsers and `GZIPInputStream` in Android. Android decodes into a bounded direct JNI buffer and uploads without transferring texture bytes through V8. Binary APK assets avoid another ZIP decompression layer. Both check gzip corruption and exact output size before upload. Hosts without native decompression retain the checked fflate decoder with 64 KiB input chunks. Float16 texture components are still checked for finiteness.
