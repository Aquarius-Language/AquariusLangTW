# PBR and model import

`Models` / `模型` and `PBR` are Aquarius source modules of the graphics library
(`aqua/Models.aqua`, `aqua/PBR.aqua`), linked into applications by the compiler when
they import them. Their parsing, number conversion, normal generation, polygon
validation, ear clipping, material validation and mesh assembly execute as ordinary
compiled Wasm functions on every target. GPU shading uses the same WGSL everywhere.

Include a literal `匯入("Models")`, `匯入("模型")` or `匯入("PBR")` somewhere in
your compiled sources. Linking includes transitive dependencies and is deterministic.
Computed module names require a literal declaration elsewhere in the application.
`.aquarius/` is reserved for these compiler-linked modules. A source-free `.wasm`
contains all library code and shaders; library imports create independent scopes.

```text
變數 models = 匯入("Models");
變數 pbr = 匯入("PBR");
變數 model = models.LoadAssetOBJ(目前工作目錄 + "/assets/model.obj");
如果 (model["ok"]) {
    變數 mesh = pbr.CreateMesh(canvas, model);
    變數 shader = pbr.CreateShader(canvas);
    pbr.SetMaterial(shader, pbr.Material([0.8d, 0.3d, 0.1d], 1.0d, 0.25d));
    canvas.shader(shader);
    canvas.shape(mesh);
}
```

The canvas must belong to the current Processing runtime. Create its window first,
use `P3D` for depth testing, and bracket offscreen rendering with beginDraw/endDraw.
Retained mesh construction belongs in setup. Normals and UVs survive model assembly;
the vertex shader transforms normals with the inverse transpose of model/view.

| Models API | Result |
|---|---|
| ParseOBJ(text) / 解析OBJ | Parsed mesh or diagnostic |
| ParseMTL(text) / 解析MTL | Parsed material array or diagnostic |
| LoadOBJ(resource) / 載入OBJ | Read a Files-owned resource as strict UTF-8, then parse |
| LoadMTL(resource) / 載入MTL | Read a Files-owned resource as strict UTF-8, then parse |
| LoadAssetOBJ(path) / 載入資產OBJ | Read a bundled OBJ through Files.ReadAssetText |
| LoadAssetMTL(path) / 載入資產MTL | Read a bundled MTL through Files.ReadAssetText |

Parsing failures return `{ok:false,error:string,line:integer}`; line 0 denotes a
whole-document or size error. Resource I/O failures are normal language errors.
Successful OBJ records contain `ok`, `vertices`, `groups`, `mtllib`, `bounds` and
`triangleCount`. Each expanded triangle vertex is `[x,y,z,nx,ny,nz,u,v]`.
Groups contain `name`, `material`, `start`, `count` (vertex offsets/counts).
Bounds use `min` and `max` arrays in source units. UVs and coordinate handedness
are preserved. Explicit normals are normalized; absent normals use flat face
normals. `s` is accepted as metadata; smoothing groups do not synthesize smooth
normals. Export explicit normals when smooth surfaces are required.

OBJ supports `v xyz`, `vt uv`, `vn xyz`, all four standard face-reference forms,
positive 1-based and negative relative indices, `o`, single-name `g`, `usemtl`,
`mtllib`, comments, BOM, whitespace, CRLF and decimal/exponent numbers. Planar
simple polygons, including concave faces and either winding, use ear clipping.
Degenerate, self-intersecting and nonplanar faces fail. Homogeneous vertices,
third texture coordinates, multiple group names, freeform curves and unknown
directives fail explicitly. The source format is documented by the
[Library of Congress](https://www.loc.gov/preservation/digital/formats/fdd/fdd000507.shtml).

MTL supports `newmtl`, linear RGB `Kd`, PBR `Pm`/`Pr`, and legacy `Ns` converted
to roughness using sqrt(2/(Ns+2)). `Ka`, `Ks`, `Ke`, `Ni`, `illum` are accepted
metadata with no rendering effect. Parsed records contain `name`, `baseColor`,
`metallic`, `roughness`. Duplicate names fail. Texture maps and transparency fail
explicitly. `mtllib` names are returned as metadata and never cause implicit file
reads. Read material files explicitly and select a matching material for each
group from CreateMeshes. No glTF/GLB, animation, skinning or texture-map import
is provided by this initial polygon/material importer.

Limits: 2 Mi UTF-16 units of text, 16,384 units per line, 256 per token,
100,000 of each position/normal/UV attribute, 100,000 triangles, 256 corners
per face, 1,024 groups, 256 materials. Coordinates must be finite and have
magnitude <=100,000,000; exponents are -38..38. A model must have polygon faces.
Very small zero-length normals and numerically degenerate faces fail.

| PBR API | Result |
|---|---|
| Material(linearRGB,metallic,roughness) / 材質 | Validated record with ok, or diagnostic |
| CreateShader(canvas) / 建立著色器 | Initialized Processing shader |
| SetMaterial(shader,material) / 設定材質 | Boolean; resets exposure=1, ambient=0.08 |
| SetLight(shader,worldDirection,radianceRGB) / 設定光源 | Boolean; direction points toward light |
| SetEnvironment(shader,material,ambient,exposure,emissiveRGB) / 設定環境 | Boolean |
| CreateMesh(canvas,model) / 建立網格 | One retained PShape, or input parse diagnostic |
| CreateMeshes(canvas,model) / 建立網格群 | Array of name/material/shape records |

RGB/metallic/roughness are 0..1. Roughness 0 is valid and the shader floors it to
0.045 to avoid singular highlights. Ambient is 0..1, exposure 0.001..100,
radiance/emissive 0..100,000. Light direction is a finite nonzero three-vector.
Range/NaN validation returns false before writing uniforms. Wrong value types,
foreign or disposed GPU handles raise language errors. Materials are opaque.

The shader combines GGX distribution, height-correlated Smith visibility,
Schlick Fresnel and energy-conserving Lambert diffuse in a metallic/roughness
workflow, following the [Khronos BRDF model](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#appendix-b-brdf-implementation).
Material colors and radiance are linear; Processing vertex colors are decoded
from sRGB. Reinhard tone mapping and the sRGB transfer function produce display
values. Ambient is a constant diffuse approximation; there is no environment
map, image-based lighting, shadows or HDR framebuffer in this basic renderer.

Files.ReadAssetBytes(path) / 讀取資產位元組 and ReadAssetText(path,encoding) /
讀取資產文字 (the application library) read packaged assets on every target, and
files beside the application on native targets. Selected user documents continue
to use Files.ReadText and opaque resource handles.

The [example](../../../examples/pbr_models/main.aqua) shows six materials and a CC0
OBJ/MTL sculpture. The library's `tests/` cover parsing, validation and rendering
(`scripts/test-libraries.ps1 -Libraries graphics -Devices`; GPU captures need a
WebGPU adapter).
