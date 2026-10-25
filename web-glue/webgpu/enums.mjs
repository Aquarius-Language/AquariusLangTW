// webgpu.h enum values and their WebGPU (JavaScript) strings: each table is indexed by the C value, so
// Table[value] is the string and code(Table, string) the value. An entry left undefined (the webgpu.h *_Undefined
// values) lets WebGPU apply its default. Values follow wgpu-native 29.0.1.1's webgpu.h.

const ASTC = ['4x4', '5x4', '5x5', '6x5', '6x6', '8x5', '8x6', '8x8', '10x5', '10x6', '10x8', '10x10', '12x10', '12x12'];

export const TextureFormat = [undefined,
    'r8unorm', 'r8snorm', 'r8uint', 'r8sint', 'r16unorm', 'r16snorm', 'r16uint', 'r16sint', 'r16float',               // 0x01
    'rg8unorm', 'rg8snorm', 'rg8uint', 'rg8sint', 'r32float', 'r32uint', 'r32sint',                                    // 0x0A
    'rg16unorm', 'rg16snorm', 'rg16uint', 'rg16sint', 'rg16float',                                                     // 0x11
    'rgba8unorm', 'rgba8unorm-srgb', 'rgba8snorm', 'rgba8uint', 'rgba8sint', 'bgra8unorm', 'bgra8unorm-srgb',          // 0x16
    'rgb10a2uint', 'rgb10a2unorm', 'rg11b10ufloat', 'rgb9e5ufloat', 'rg32float', 'rg32uint', 'rg32sint',               // 0x1D
    'rgba16unorm', 'rgba16snorm', 'rgba16uint', 'rgba16sint', 'rgba16float', 'rgba32float', 'rgba32uint', 'rgba32sint', // 0x24
    'stencil8', 'depth16unorm', 'depth24plus', 'depth24plus-stencil8', 'depth32float', 'depth32float-stencil8',         // 0x2C
    'bc1-rgba-unorm', 'bc1-rgba-unorm-srgb', 'bc2-rgba-unorm', 'bc2-rgba-unorm-srgb', 'bc3-rgba-unorm',                // 0x32
    'bc3-rgba-unorm-srgb', 'bc4-r-unorm', 'bc4-r-snorm', 'bc5-rg-unorm', 'bc5-rg-snorm', 'bc6h-rgb-ufloat',
    'bc6h-rgb-float', 'bc7-rgba-unorm', 'bc7-rgba-unorm-srgb',
    'etc2-rgb8unorm', 'etc2-rgb8unorm-srgb', 'etc2-rgb8a1unorm', 'etc2-rgb8a1unorm-srgb', 'etc2-rgba8unorm',          // 0x40
    'etc2-rgba8unorm-srgb', 'eac-r11unorm', 'eac-r11snorm', 'eac-rg11unorm', 'eac-rg11snorm',
    ...ASTC.flatMap((size) => [`astc-${size}-unorm`, `astc-${size}-unorm-srgb`]),                                     // 0x4A..0x65
];

export const VertexFormat = [undefined,
    'uint8', 'uint8x2', 'uint8x4', 'sint8', 'sint8x2', 'sint8x4', 'unorm8', 'unorm8x2', 'unorm8x4', 'snorm8', 'snorm8x2', 'snorm8x4',
    'uint16', 'uint16x2', 'uint16x4', 'sint16', 'sint16x2', 'sint16x4', 'unorm16', 'unorm16x2', 'unorm16x4',
    'snorm16', 'snorm16x2', 'snorm16x4', 'float16', 'float16x2', 'float16x4',
    'float32', 'float32x2', 'float32x3', 'float32x4', 'uint32', 'uint32x2', 'uint32x3', 'uint32x4',
    'sint32', 'sint32x2', 'sint32x3', 'sint32x4', 'unorm10-10-10-2', 'unorm8x4-bgra',
];

export const FeatureName = [undefined,
    'core-features-and-limits', 'depth-clip-control', 'depth32float-stencil8', 'texture-compression-bc',
    'texture-compression-bc-sliced-3d', 'texture-compression-etc2', 'texture-compression-astc',
    'texture-compression-astc-sliced-3d', 'timestamp-query', 'indirect-first-instance', 'shader-f16',
    'rg11b10ufloat-renderable', 'bgra8unorm-storage', 'float32-filterable', 'float32-blendable', 'clip-distances',
    'dual-source-blending', 'subgroups', 'texture-formats-tier1', 'texture-formats-tier2', 'primitive-index',
    'texture-component-swizzle',
];

export const WGSLLanguageFeatureName = [undefined,
    'readonly_and_readwrite_storage_textures', 'packed_4x8_integer_dot_product', 'unrestricted_pointer_parameters',
    'pointer_composite_access', 'uniform_buffer_standard_layout', 'subgroup_id', 'texture_and_sampler_let',
    'subgroup_uniformity', 'texture_formats_tier1', 'linear_indexing',
];

export const TextureDimension = [undefined, '1d', '2d', '3d'];
export const TextureViewDimension = [undefined, '1d', '2d', '2d-array', 'cube', 'cube-array', '3d'];
export const TextureAspect = [undefined, 'all', 'stencil-only', 'depth-only'];
export const AddressMode = [undefined, 'clamp-to-edge', 'repeat', 'mirror-repeat'];
export const FilterMode = [undefined, 'nearest', 'linear'];
export const MipmapFilterMode = [undefined, 'nearest', 'linear'];
export const CompareFunction = [undefined, 'never', 'less', 'equal', 'less-equal', 'greater', 'not-equal', 'greater-equal', 'always'];
export const BlendOperation = [undefined, 'add', 'subtract', 'reverse-subtract', 'min', 'max'];
export const BlendFactor = [undefined, 'zero', 'one', 'src', 'one-minus-src', 'src-alpha', 'one-minus-src-alpha', 'dst',
    'one-minus-dst', 'dst-alpha', 'one-minus-dst-alpha', 'src-alpha-saturated', 'constant', 'one-minus-constant', 'src1',
    'one-minus-src1', 'src1-alpha', 'one-minus-src1-alpha'];
export const StencilOperation = [undefined, 'keep', 'zero', 'replace', 'invert', 'increment-clamp', 'decrement-clamp',
    'increment-wrap', 'decrement-wrap'];
// Binding types: 0 is BindingNotUsed and 1 Undefined (WebGPU's default type).
export const BufferBindingType = [undefined, undefined, 'uniform', 'storage', 'read-only-storage'];
export const SamplerBindingType = [undefined, undefined, 'filtering', 'non-filtering', 'comparison'];
export const TextureSampleType = [undefined, undefined, 'float', 'unfilterable-float', 'depth', 'sint', 'uint'];
export const StorageTextureAccess = [undefined, undefined, 'write-only', 'read-only', 'read-write'];
export const PrimitiveTopology = [undefined, 'point-list', 'line-list', 'line-strip', 'triangle-list', 'triangle-strip'];
export const IndexFormat = [undefined, 'uint16', 'uint32'];
export const FrontFace = [undefined, 'ccw', 'cw'];
export const CullMode = [undefined, 'none', 'front', 'back'];
export const VertexStepMode = [undefined, 'vertex', 'instance'];
export const LoadOp = [undefined, 'load', 'clear'];
export const StoreOp = [undefined, 'store', 'discard'];
export const QueryType = [undefined, 'occlusion', 'timestamp'];
export const PowerPreference = [undefined, 'low-power', 'high-performance'];
export const FeatureLevel = [undefined, 'compatibility', 'core'];
export const ErrorFilter = [undefined, 'validation', 'out-of-memory', 'internal'];
export const ComponentSwizzle = [undefined, '0', '1', 'r', 'g', 'b', 'a'];
// Canvas alpha modes: Auto (0) and Inherit (4) use the canvas default, opaque; WebGPU has no unpremultiplied mode.
export const CompositeAlphaMode = [undefined, 'opaque', 'premultiplied', undefined, undefined];
export const PredefinedColorSpace = [undefined, 'srgb', 'display-p3'];
export const ToneMappingMode = [undefined, 'standard', 'extended'];
export const CompilationMessageType = [undefined, 'error', 'warning', 'info'];
export const BufferMapState = [undefined, 'unmapped', 'pending', 'mapped'];

const codes = new Map();
/** The webgpu.h value of a WebGPU string in `table`, or 0 (the *_Undefined value) when it has none. */
export function code(table, name) {
    let lookup = codes.get(table);
    if (!lookup) codes.set(table, lookup = new Map(table.map((entry, index) => [entry, index]).filter(([entry]) => entry)));
    return lookup.get(name) ?? 0;
}
