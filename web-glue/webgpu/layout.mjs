// The C structs of webgpu.h (wgpu-native 29.0.1.1) as they lie in wasm32 memory, and reading/writing them.
// Each layout lists its fields in declaration order with their C type; offsets follow the C rules (each field at a
// multiple of its alignment, the size rounded up to the largest alignment), so no offset is written by hand.
// wasm32: pointers, handles, size_t, enums and WGPUBool are 4 bytes; uint64_t and double are 8-byte aligned;
// WGPUStringView is {const char *data; size_t length}.

const SCALARS = {
    u16: [2, 2], u32: [4, 4], i32: [4, 4], f32: [4, 4], f64: [8, 8], u64: [8, 8],
    ptr: [4, 4],    // pointers and object handles
    size: [4, 4],   // size_t
    enum: [4, 4],
    bool: [4, 4],   // WGPUBool
    str: [8, 4],    // WGPUStringView
};
export const SIZE_MAX = 0xFFFFFFFF;          // WGPU_STRLEN and WGPU_WHOLE_MAP_SIZE on wasm32
export const U32_UNDEFINED = 0xFFFFFFFF;     // WGPU_{MIP_LEVEL_COUNT,ARRAY_LAYER_COUNT,DEPTH_SLICE,...}_UNDEFINED
const U64_MAX = 0xFFFFFFFFFFFFFFFFn;         // WGPU_WHOLE_SIZE and WGPU_LIMIT_U64_UNDEFINED

/** A C struct: `fields` maps names (in declaration order) to a scalar type name or another layout. */
export function struct(fields) {
    const layout = { fields: [], offset: {}, size: 0, align: 1 };
    let offset = 0;
    for (const [name, type] of Object.entries(fields)) {
        const [size, align] = typeof type === 'string' ? SCALARS[type] : [type.size, type.align];
        offset = Math.ceil(offset / align) * align;
        layout.fields.push({ name, type, offset });
        layout.offset[name] = offset;
        offset += size;
        layout.align = Math.max(layout.align, align);
    }
    layout.size = Math.ceil(offset / layout.align) * layout.align;
    return layout;
}

const Chained = struct({ next: 'ptr', sType: 'enum' });
const labeled = (fields = {}) => struct({ nextInChain: 'ptr', label: 'str', ...fields });
const UncapturedErrorCallbackInfo = struct({ nextInChain: 'ptr', callback: 'ptr', userdata1: 'ptr', userdata2: 'ptr' });
const CallbackInfo = struct({ nextInChain: 'ptr', mode: 'enum', callback: 'ptr', userdata1: 'ptr', userdata2: 'ptr' });
const Extent3D = struct({ width: 'u32', height: 'u32', depthOrArrayLayers: 'u32' });
const Origin3D = struct({ x: 'u32', y: 'u32', z: 'u32' });
const Color = struct({ r: 'f64', g: 'f64', b: 'f64', a: 'f64' });
const BlendComponent = struct({ operation: 'enum', srcFactor: 'enum', dstFactor: 'enum' });
const StencilFaceState = struct({ compare: 'enum', failOp: 'enum', depthFailOp: 'enum', passOp: 'enum' });
const TexelCopyBufferLayout = struct({ offset: 'u64', bytesPerRow: 'u32', rowsPerImage: 'u32' });
const programmableStage = (fields = {}) => struct({ nextInChain: 'ptr', module: 'ptr', entryPoint: 'str', constantCount: 'size', constants: 'ptr', ...fields });

/** Every webgpu.h struct the glue reads or writes, named as in webgpu.h without the WGPU prefix. */
export const S = {
    Chained, CallbackInfo, Extent3D, Origin3D, Color, TexelCopyBufferLayout,
    StringView: struct({ data: 'ptr', length: 'size' }),
    UncapturedErrorCallbackInfo,
    FutureWaitInfo: struct({ future: 'u64', completed: 'bool' }),
    InstanceLimits: struct({ nextInChain: 'ptr', timedWaitAnyMaxCount: 'size' }),
    SupportedFeatures: struct({ featureCount: 'size', features: 'ptr' }), // also the instance and WGSL feature lists
    AdapterInfo: struct({
        nextInChain: 'ptr', vendor: 'str', architecture: 'str', device: 'str', description: 'str', backendType: 'enum',
        adapterType: 'enum', vendorID: 'u32', deviceID: 'u32', subgroupMinSize: 'u32', subgroupMaxSize: 'u32',
    }),
    // Field names are those of GPUSupportedLimits.
    Limits: struct({
        nextInChain: 'ptr', maxTextureDimension1D: 'u32', maxTextureDimension2D: 'u32', maxTextureDimension3D: 'u32',
        maxTextureArrayLayers: 'u32', maxBindGroups: 'u32', maxBindGroupsPlusVertexBuffers: 'u32', maxBindingsPerBindGroup: 'u32',
        maxDynamicUniformBuffersPerPipelineLayout: 'u32', maxDynamicStorageBuffersPerPipelineLayout: 'u32',
        maxSampledTexturesPerShaderStage: 'u32', maxSamplersPerShaderStage: 'u32', maxStorageBuffersPerShaderStage: 'u32',
        maxStorageTexturesPerShaderStage: 'u32', maxUniformBuffersPerShaderStage: 'u32', maxUniformBufferBindingSize: 'u64',
        maxStorageBufferBindingSize: 'u64', minUniformBufferOffsetAlignment: 'u32', minStorageBufferOffsetAlignment: 'u32',
        maxVertexBuffers: 'u32', maxBufferSize: 'u64', maxVertexAttributes: 'u32', maxVertexBufferArrayStride: 'u32',
        maxInterStageShaderVariables: 'u32', maxColorAttachments: 'u32', maxColorAttachmentBytesPerSample: 'u32',
        maxComputeWorkgroupStorageSize: 'u32', maxComputeInvocationsPerWorkgroup: 'u32', maxComputeWorkgroupSizeX: 'u32',
        maxComputeWorkgroupSizeY: 'u32', maxComputeWorkgroupSizeZ: 'u32', maxComputeWorkgroupsPerDimension: 'u32',
        maxImmediateSize: 'u32',
    }),
    CompatibilityModeLimits: struct({
        chain: Chained, maxStorageBuffersInVertexStage: 'u32', maxStorageTexturesInVertexStage: 'u32',
        maxStorageBuffersInFragmentStage: 'u32', maxStorageTexturesInFragmentStage: 'u32',
    }),
    RequestAdapterOptions: struct({
        nextInChain: 'ptr', featureLevel: 'enum', powerPreference: 'enum', forceFallbackAdapter: 'bool', backendType: 'enum',
        compatibleSurface: 'ptr',
    }),
    RequestAdapterWebXROptions: struct({ chain: Chained, xrCompatible: 'bool' }),
    DeviceDescriptor: labeled({
        requiredFeatureCount: 'size', requiredFeatures: 'ptr', requiredLimits: 'ptr', defaultQueue: labeled(),
        deviceLostCallbackInfo: CallbackInfo, uncapturedErrorCallbackInfo: UncapturedErrorCallbackInfo,
    }),
    // CommandBuffer, CommandEncoder, RenderBundle, ShaderModule and Surface descriptors: a chain and a label.
    Labeled: labeled(),
    ShaderSourceWGSL: struct({ chain: Chained, code: 'str' }),
    EmscriptenSurfaceSourceCanvasHTMLSelector: struct({ chain: Chained, selector: 'str' }),
    BufferDescriptor: labeled({ usage: 'u64', size: 'u64', mappedAtCreation: 'bool' }),
    TextureDescriptor: labeled({
        usage: 'u64', dimension: 'enum', size: Extent3D, format: 'enum', mipLevelCount: 'u32', sampleCount: 'u32',
        viewFormatCount: 'size', viewFormats: 'ptr',
    }),
    TextureBindingViewDimension: struct({ chain: Chained, textureBindingViewDimension: 'enum' }),
    TextureViewDescriptor: labeled({
        format: 'enum', dimension: 'enum', baseMipLevel: 'u32', mipLevelCount: 'u32', baseArrayLayer: 'u32',
        arrayLayerCount: 'u32', aspect: 'enum', usage: 'u64',
    }),
    TextureComponentSwizzleDescriptor: struct({ chain: Chained, swizzle: struct({ r: 'enum', g: 'enum', b: 'enum', a: 'enum' }) }),
    SamplerDescriptor: labeled({
        addressModeU: 'enum', addressModeV: 'enum', addressModeW: 'enum', magFilter: 'enum', minFilter: 'enum',
        mipmapFilter: 'enum', lodMinClamp: 'f32', lodMaxClamp: 'f32', compare: 'enum', maxAnisotropy: 'u16',
    }),
    BindGroupLayoutEntry: struct({
        nextInChain: 'ptr', binding: 'u32', visibility: 'u64', bindingArraySize: 'u32',
        buffer: struct({ nextInChain: 'ptr', type: 'enum', hasDynamicOffset: 'bool', minBindingSize: 'u64' }),
        sampler: struct({ nextInChain: 'ptr', type: 'enum' }),
        texture: struct({ nextInChain: 'ptr', sampleType: 'enum', viewDimension: 'enum', multisampled: 'bool' }),
        storageTexture: struct({ nextInChain: 'ptr', access: 'enum', format: 'enum', viewDimension: 'enum' }),
    }),
    BindGroupLayoutDescriptor: labeled({ entryCount: 'size', entries: 'ptr' }),
    BindGroupEntry: struct({
        nextInChain: 'ptr', binding: 'u32', buffer: 'ptr', offset: 'u64', size: 'u64', sampler: 'ptr', textureView: 'ptr',
    }),
    ExternalTextureBindingEntry: struct({ chain: Chained, externalTexture: 'ptr' }),
    BindGroupDescriptor: labeled({ layout: 'ptr', entryCount: 'size', entries: 'ptr' }),
    PipelineLayoutDescriptor: labeled({ bindGroupLayoutCount: 'size', bindGroupLayouts: 'ptr', immediateSize: 'u32' }),
    ConstantEntry: struct({ nextInChain: 'ptr', key: 'str', value: 'f64' }),
    ComputePipelineDescriptor: labeled({ layout: 'ptr', compute: programmableStage() }),
    VertexAttribute: struct({ nextInChain: 'ptr', format: 'enum', offset: 'u64', shaderLocation: 'u32' }),
    VertexBufferLayout: struct({ nextInChain: 'ptr', stepMode: 'enum', arrayStride: 'u64', attributeCount: 'size', attributes: 'ptr' }),
    DepthStencilState: struct({
        nextInChain: 'ptr', format: 'enum', depthWriteEnabled: 'enum', depthCompare: 'enum', stencilFront: StencilFaceState,
        stencilBack: StencilFaceState, stencilReadMask: 'u32', stencilWriteMask: 'u32', depthBias: 'i32',
        depthBiasSlopeScale: 'f32', depthBiasClamp: 'f32',
    }),
    BlendState: struct({ color: BlendComponent, alpha: BlendComponent }),
    ColorTargetState: struct({ nextInChain: 'ptr', format: 'enum', blend: 'ptr', writeMask: 'u64' }),
    FragmentState: programmableStage({ targetCount: 'size', targets: 'ptr' }),
    RenderPipelineDescriptor: labeled({
        layout: 'ptr', vertex: programmableStage({ bufferCount: 'size', buffers: 'ptr' }),
        primitive: struct({ nextInChain: 'ptr', topology: 'enum', stripIndexFormat: 'enum', frontFace: 'enum', cullMode: 'enum', unclippedDepth: 'bool' }),
        depthStencil: 'ptr',
        multisample: struct({ nextInChain: 'ptr', count: 'u32', mask: 'u32', alphaToCoverageEnabled: 'bool' }),
        fragment: 'ptr',
    }),
    RenderPassColorAttachment: struct({
        nextInChain: 'ptr', view: 'ptr', depthSlice: 'u32', resolveTarget: 'ptr', loadOp: 'enum', storeOp: 'enum', clearValue: Color,
    }),
    RenderPassDepthStencilAttachment: struct({
        nextInChain: 'ptr', view: 'ptr', depthLoadOp: 'enum', depthStoreOp: 'enum', depthClearValue: 'f32', depthReadOnly: 'bool',
        stencilLoadOp: 'enum', stencilStoreOp: 'enum', stencilClearValue: 'u32', stencilReadOnly: 'bool',
    }),
    PassTimestampWrites: struct({ nextInChain: 'ptr', querySet: 'ptr', beginningOfPassWriteIndex: 'u32', endOfPassWriteIndex: 'u32' }),
    RenderPassDescriptor: labeled({
        colorAttachmentCount: 'size', colorAttachments: 'ptr', depthStencilAttachment: 'ptr', occlusionQuerySet: 'ptr', timestampWrites: 'ptr',
    }),
    RenderPassMaxDrawCount: struct({ chain: Chained, maxDrawCount: 'u64' }),
    ComputePassDescriptor: labeled({ timestampWrites: 'ptr' }),
    TexelCopyBufferInfo: struct({ layout: TexelCopyBufferLayout, buffer: 'ptr' }),
    TexelCopyTextureInfo: struct({ texture: 'ptr', mipLevel: 'u32', origin: Origin3D, aspect: 'enum' }),
    QuerySetDescriptor: labeled({ type: 'enum', count: 'u32' }),
    RenderBundleEncoderDescriptor: labeled({
        colorFormatCount: 'size', colorFormats: 'ptr', depthStencilFormat: 'enum', sampleCount: 'u32', depthReadOnly: 'bool', stencilReadOnly: 'bool',
    }),
    SurfaceConfiguration: struct({
        nextInChain: 'ptr', device: 'ptr', format: 'enum', usage: 'u64', width: 'u32', height: 'u32', viewFormatCount: 'size',
        viewFormats: 'ptr', alphaMode: 'enum', presentMode: 'enum',
    }),
    SurfaceColorManagement: struct({ chain: Chained, colorSpace: 'enum', toneMappingMode: 'enum' }),
    SurfaceCapabilities: struct({
        nextInChain: 'ptr', usages: 'u64', formatCount: 'size', formats: 'ptr', presentModeCount: 'size', presentModes: 'ptr',
        alphaModeCount: 'size', alphaModes: 'ptr',
    }),
    SurfaceTexture: struct({ nextInChain: 'ptr', texture: 'ptr', status: 'enum' }),
    CompilationInfo: struct({ nextInChain: 'ptr', messageCount: 'size', messages: 'ptr' }),
    CompilationMessage: struct({
        nextInChain: 'ptr', message: 'str', type: 'enum', lineNum: 'u64', linePos: 'u64', offset: 'u64', length: 'u64',
    }),
};

/** WGPUSType values of the chained structs the glue understands. */
export const SType = {
    ShaderSourceSPIRV: 0x1, ShaderSourceWGSL: 0x2, RenderPassMaxDrawCount: 0x3, SurfaceColorManagement: 0xA,
    RequestAdapterWebXROptions: 0xB, TextureComponentSwizzleDescriptor: 0xC, ExternalTextureBindingLayout: 0xD,
    ExternalTextureBindingEntry: 0xE, CompatibilityModeLimits: 0xF, TextureBindingViewDimension: 0x10,
    EmscriptenSurfaceSourceCanvasHTMLSelector: 0x40000,
};

const encoder = new TextEncoder();

/** Reads and writes webgpu.h structs in the application's memory. */
export class Memory {
    constructor(context) { this.context = context; }

    /** A WGPUStringView's text: undefined for the null view {NULL, WGPU_STRLEN}, '' for an empty one. */
    string(pointer) { const view = this.context.view; return this.decode(view.getUint32(pointer, true), view.getUint32(pointer + 4, true)); }
    decode(data, length) {
        if (length === 0) return '';
        if (!data) return undefined;
        return this.context.text(data, length === SIZE_MAX ? -1 : length);
    }

    /** The struct at `pointer` as an object of its fields; u64 fields become numbers, UINT64_MAX becomes undefined. */
    read(layout, pointer, view = this.context.view) {
        const result = {};
        for (const { name, type, offset } of layout.fields) {
            const at = pointer + offset;
            switch (type) {
                case 'u16': result[name] = view.getUint16(at, true); break;
                case 'u32': case 'ptr': case 'size': case 'enum': result[name] = view.getUint32(at, true); break;
                case 'i32': result[name] = view.getInt32(at, true); break;
                case 'bool': result[name] = view.getUint32(at, true) !== 0; break;
                case 'f32': result[name] = view.getFloat32(at, true); break;
                case 'f64': result[name] = view.getFloat64(at, true); break;
                case 'u64': { const value = view.getBigUint64(at, true); result[name] = value === U64_MAX ? undefined : Number(value); break; }
                case 'str': result[name] = this.decode(view.getUint32(at, true), view.getUint32(at + 4, true)); break;
                default: result[name] = this.read(type, at, view);
            }
        }
        return result;
    }
    /** `count` consecutive structs. */
    array(layout, pointer, count) {
        const view = this.context.view, items = [];
        for (let i = 0; i < count; i++) items.push(this.read(layout, pointer + i * layout.size, view));
        return items;
    }
    /** `count` uint32_t values (handles, enums). */
    u32s(pointer, count) { return count ? Array.from(this.context.u32.subarray(pointer >>> 2, (pointer >>> 2) + count)) : []; }

    /** Writes the given fields of a struct; a 'str' field takes [data, length]. Other fields are left as they are. */
    write(layout, pointer, values) {
        const view = this.context.view;
        for (const { name, type, offset } of layout.fields) {
            if (!(name in values)) continue;
            const at = pointer + offset, value = values[name];
            switch (type) {
                case 'u16': view.setUint16(at, value, true); break;
                case 'u32': case 'ptr': case 'size': case 'enum': view.setUint32(at, value, true); break;
                case 'i32': view.setInt32(at, value, true); break;
                case 'bool': view.setUint32(at, value ? 1 : 0, true); break;
                case 'f32': view.setFloat32(at, value, true); break;
                case 'f64': view.setFloat64(at, value, true); break;
                case 'u64': view.setBigUint64(at, BigInt(value), true); break;
                case 'str': view.setUint32(at, value[0], true); view.setUint32(at + 4, value[1], true); break;
                default: throw new Error(`webgpu: cannot write nested struct ${name}`);
            }
        }
    }

    /** The chained structs from a nextInChain pointer: sType -> pointer to the struct. */
    chain(pointer) {
        const found = new Map(), view = this.context.view;
        for (let p = pointer; p; p = view.getUint32(p, true)) found.set(view.getUint32(p + 4, true), p);
        return found;
    }

    /** malloc'd memory holding the UTF-8 strings one after another, NUL-terminated: [block, [[data, length]...]]. */
    strings(texts) {
        const encoded = texts.map((text) => encoder.encode(text ?? ''));
        const block = this.context.call('malloc', encoded.reduce((sum, bytes) => sum + bytes.length + 1, 0) || 1);
        const bytes = this.context.bytes, views = [];
        let at = block;
        for (const data of encoded) { bytes.set(data, at); bytes[at + data.length] = 0; views.push([at, data.length]); at += data.length + 1; }
        return [block, views];
    }
}
