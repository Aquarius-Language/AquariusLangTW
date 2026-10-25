// webgpu.h on the browser's WebGPU (navigator.gpu): C and C++ code written against wgpu-native's webgpu.h calls these
// functions by their standard names (the loader binds a side module's `env` imports to glue functions of the same
// name), so one code path runs on Windows, Linux, Android and the web. Each function marshals its arguments from
// memory, makes one WebGPU call and hands the result back; layout.mjs reads the structs, enums.mjs maps the enums.
//
// Conventions (wasm32): handles are glue handles of {gpu, refs, instance} records (AddRef/Release count them; the
// WebGPU object is left to the garbage collector once no C reference remains, since WebGPU keeps it alive while
// bind groups or commands use it). uint64_t arguments arrive as BigInt, WGPUFuture returns as BigInt. Asynchronous
// operations settle their promise, then call the C callback: AllowSpontaneous at once, AllowProcessEvents in
// wgpuInstanceProcessEvents (or wgpuInstanceWaitAny), WaitAnyOnly in wgpuInstanceWaitAny, which only polls (timeout
// 0) because the page's single thread cannot block. Mapped ranges are malloc'd copies of the mapping, written back
// on unmap when the buffer is mapped for writing.
import { registerGlue } from '../core/glue.mjs';
import { Memory, S, SType, SIZE_MAX, U32_UNDEFINED } from './layout.mjs';
import * as E from './enums.mjs';

const STATUS_SUCCESS = 1, STATUS_ERROR = 2;
const MODE_WAIT_ANY_ONLY = 1, MODE_ALLOW_SPONTANEOUS = 3;
const WAIT_SUCCESS = 1, WAIT_TIMED_OUT = 2, WAIT_ERROR = 3;
const MAP_WRITE = 2;
const U64_MAX = 0xFFFFFFFFFFFFFFFFn;
const NO_CALLBACK = { mode: 0, callback: 0, userdata1: 0, userdata2: 0 };
const encoder = new TextEncoder();
// Every webgpu.h object type; each has AddRef and Release, all but Adapter and Instance have SetLabel.
const OBJECTS = ['Adapter', 'BindGroup', 'BindGroupLayout', 'Buffer', 'CommandBuffer', 'CommandEncoder', 'ComputePassEncoder',
    'ComputePipeline', 'Device', 'ExternalTexture', 'Instance', 'PipelineLayout', 'QuerySet', 'Queue', 'RenderBundle',
    'RenderBundleEncoder', 'RenderPassEncoder', 'RenderPipeline', 'Sampler', 'ShaderModule', 'Surface', 'Texture', 'TextureView'];

const optional32 = (value) => (value === U32_UNDEFINED ? undefined : value);
// uint64_t arguments arrive as signed BigInts (wasm i64): UINT64_MAX is -1n.
const u64 = (value) => Number(BigInt.asUintN(64, value));
const size64 = (value) => (BigInt.asUintN(64, value) === U64_MAX ? undefined : u64(value)); // WGPU_WHOLE_SIZE: undefined
/** WGPUErrorType of a GPUError (null: NoError). */
const errorType = (error) => (!error ? 1 : error.constructor.name === 'GPUValidationError' ? 2
    : error.constructor.name === 'GPUOutOfMemoryError' ? 3 : error.constructor.name === 'GPUInternalError' ? 4 : 5);

registerGlue('webgpu', (context) => {
    const memory = new Memory(context);
    const malloc = (bytes) => context.call('malloc', bytes);
    const free = (pointer) => context.call('free', pointer);

    // ---- Handles ------------------------------------------------------------------------------------------------
    /** A new handle with one reference; `instance` is the WGPUInstance record whose events deliver its callbacks. */
    const wrap = (gpu, instance, fields) => context.retain({ gpu, instance, refs: 1, ...fields });
    const record = (handle) => context.object(handle);
    const gpu = (handle) => (handle ? context.object(handle).gpu : undefined);
    const gpus = (pointer, count) => memory.u32s(pointer, count).map((handle) => gpu(handle) ?? null);
    const addRef = (handle) => { record(handle).refs++; };
    const release = (handle) => {
        const r = record(handle);
        if (!r || --r.refs > 0) return;
        if (r.mapping) endMapping(r, false); // a buffer released while mapped: free its ranges
        context.release(handle);
    };
    const setLabel = (handle, label) => { record(handle).gpu.label = memory.string(label) ?? ''; };

    // ---- Futures and callbacks ------------------------------------------------------------------------------------
    let lastFuture = 0n; // WGPUFuture ids; 0 is no future
    /** A pending operation calling back through `info` (a WGPU*CallbackInfo); returns its future record. */
    function begin(instance, info) {
        const future = { id: ++lastFuture, instance, info, call: null };
        instance.futures.set(future.id, future);
        return future;
    }
    /** The operation finished: `call(callback, userdata1, userdata2)` calls the C callback when its mode allows. */
    function settle(future, call) {
        future.call = call;
        if (future.info.mode === MODE_ALLOW_SPONTANEOUS) fire(future);
        else if (future.info.mode !== MODE_WAIT_ANY_ONLY) future.instance.ready.push(future);
        context.wake();
    }
    function fire(future) {
        if (!future.instance.futures.delete(future.id)) return; // each callback fires once
        const { callback, userdata1, userdata2 } = future.info;
        if (callback) future.call(callback, userdata1, userdata2);
    }
    /** begin + settle for a promise; `then(callback, u1, u2, value, error)` calls the C callback. */
    function track(instance, infoPointer, promise, then) {
        const future = begin(instance, memory.read(S.CallbackInfo, infoPointer));
        promise.then((value) => settle(future, (callback, u1, u2) => then(callback, u1, u2, value, null)),
            (error) => settle(future, (callback, u1, u2) => then(callback, u1, u2, undefined, error)));
        return future.id;
    }
    /** Calls `run(message, devicePointer)` with a temporary WGPUStringView of `text` (and a WGPUDevice holding `handle`),
        the by-value arguments callbacks receive as pointers on wasm32. One block: view (8), device (4), text. */
    function withMessage(text, run, handle = 0) {
        const data = encoder.encode(text ?? ''), block = malloc(12 + data.length + 1), bytes = context.bytes;
        bytes.set(data, block + 12);
        bytes[block + 12 + data.length] = 0;
        memory.write(S.StringView, block, { data: block + 12, length: data.length });
        context.view.setUint32(block + 8, handle, true);
        try { run(block, block + 8); } finally { free(block); }
    }

    // ---- Shared conversions ---------------------------------------------------------------------------------------
    const formats = (pointer, count) => memory.u32s(pointer, count).map((format) => E.TextureFormat[format]);
    /** Writes a malloc'd array of the webgpu.h values of `names` (unknown ones skipped) into a WGPUSupported*Features. */
    function writeFeatures(pointer, names, table) {
        const values = [...names].map((name) => E.code(table, name)).filter((value) => value);
        const array = values.length ? malloc(values.length * 4) : 0;
        values.forEach((value, i) => context.view.setUint32(array + i * 4, value, true));
        memory.write(S.SupportedFeatures, pointer, { featureCount: values.length, features: array });
    }
    const freeFeatures = (pointer) => free(memory.read(S.SupportedFeatures, pointer).features);
    /** GPUSupportedLimits into a WGPULimits (and a chained WGPUCompatibilityModeLimits). */
    function writeLimits(pointer, limits) {
        const values = {};
        for (const { name } of S.Limits.fields) if (name !== 'nextInChain') values[name] = limits[name] ?? 0;
        memory.write(S.Limits, pointer, values);
        const compatibility = memory.chain(memory.read(S.Limits, pointer).nextInChain).get(SType.CompatibilityModeLimits);
        if (compatibility) memory.write(S.CompatibilityModeLimits, compatibility, {
            maxStorageBuffersInVertexStage: limits.maxStorageBuffersInVertexStage ?? limits.maxStorageBuffersPerShaderStage,
            maxStorageTexturesInVertexStage: limits.maxStorageTexturesInVertexStage ?? limits.maxStorageTexturesPerShaderStage,
            maxStorageBuffersInFragmentStage: limits.maxStorageBuffersInFragmentStage ?? limits.maxStorageBuffersPerShaderStage,
            maxStorageTexturesInFragmentStage: limits.maxStorageTexturesInFragmentStage ?? limits.maxStorageTexturesPerShaderStage,
        });
        return STATUS_SUCCESS;
    }
    /** A WGPULimits as requiredLimits: the limits it sets that `supported` (GPUSupportedLimits) knows. */
    function requiredLimits(pointer, supported) {
        if (!pointer) return undefined;
        const values = memory.read(S.Limits, pointer), limits = {};
        const compatibility = memory.chain(values.nextInChain).get(SType.CompatibilityModeLimits);
        const sets = compatibility ? [values, memory.read(S.CompatibilityModeLimits, compatibility)] : [values];
        for (const set of sets) {
            for (const [name, value] of Object.entries(set)) {
                // nextInChain and chain are not limits of GPUSupportedLimits; undefined limits stay unrequested.
                if (typeof value === 'number' && value !== U32_UNDEFINED && name in supported) limits[name] = value;
            }
        }
        return limits;
    }
    /** GPUAdapterInfo into a WGPUAdapterInfo; the strings share one malloc'd block, freed by wgpuAdapterInfoFreeMembers. */
    function writeInfo(pointer, info) {
        const [, [vendor, architecture, device, description]] = memory.strings([info.vendor, info.architecture, info.device, info.description]);
        memory.write(S.AdapterInfo, pointer, {
            vendor, architecture, device, description, backendType: 2 /* WebGPU */, adapterType: info.isFallbackAdapter ? 3 /* CPU */ : 4 /* Unknown */,
            vendorID: 0, deviceID: 0, subgroupMinSize: info.subgroupMinSize ?? 0, subgroupMaxSize: info.subgroupMaxSize ?? 0,
        });
        return STATUS_SUCCESS;
    }
    const constants = (pointer, count) => (count ? Object.fromEntries(memory.array(S.ConstantEntry, pointer, count).map((c) => [c.key, c.value])) : undefined);
    const stage = (s) => ({ module: gpu(s.module), entryPoint: s.entryPoint, constants: constants(s.constants, s.constantCount) });
    const blendComponent = (c) => ({ operation: E.BlendOperation[c.operation], srcFactor: E.BlendFactor[c.srcFactor], dstFactor: E.BlendFactor[c.dstFactor] });
    const stencilFace = (f) => ({ compare: E.CompareFunction[f.compare], failOp: E.StencilOperation[f.failOp], depthFailOp: E.StencilOperation[f.depthFailOp], passOp: E.StencilOperation[f.passOp] });
    const timestampWrites = (pointer) => {
        if (!pointer) return undefined;
        const t = memory.read(S.PassTimestampWrites, pointer);
        return { querySet: gpu(t.querySet), beginningOfPassWriteIndex: optional32(t.beginningOfPassWriteIndex), endOfPassWriteIndex: optional32(t.endOfPassWriteIndex) };
    };
    const layoutOf = (handle) => (handle ? gpu(handle) : 'auto');
    function computePipelineDescriptor(pointer) {
        const d = memory.read(S.ComputePipelineDescriptor, pointer);
        return { label: d.label, layout: layoutOf(d.layout), compute: stage(d.compute) };
    }
    function renderPipelineDescriptor(pointer) {
        const d = memory.read(S.RenderPipelineDescriptor, pointer), { vertex, primitive, multisample } = d;
        const depth = d.depthStencil && memory.read(S.DepthStencilState, d.depthStencil);
        const fragment = d.fragment && memory.read(S.FragmentState, d.fragment);
        return {
            label: d.label,
            layout: layoutOf(d.layout),
            vertex: {
                ...stage(vertex),
                // A buffer without attributes and step mode is a hole (null) in the list.
                buffers: memory.array(S.VertexBufferLayout, vertex.buffers, vertex.bufferCount).map((b) => (!b.stepMode && !b.attributeCount ? null : {
                    arrayStride: b.arrayStride, stepMode: E.VertexStepMode[b.stepMode],
                    attributes: memory.array(S.VertexAttribute, b.attributes, b.attributeCount)
                        .map((a) => ({ format: E.VertexFormat[a.format], offset: a.offset, shaderLocation: a.shaderLocation })),
                })),
            },
            primitive: {
                topology: E.PrimitiveTopology[primitive.topology], stripIndexFormat: E.IndexFormat[primitive.stripIndexFormat],
                frontFace: E.FrontFace[primitive.frontFace], cullMode: E.CullMode[primitive.cullMode], unclippedDepth: primitive.unclippedDepth || undefined,
            },
            depthStencil: depth ? {
                format: E.TextureFormat[depth.format],
                depthWriteEnabled: depth.depthWriteEnabled === 2 /* OptionalBool_Undefined */ ? undefined : depth.depthWriteEnabled === 1,
                depthCompare: E.CompareFunction[depth.depthCompare], stencilFront: stencilFace(depth.stencilFront), stencilBack: stencilFace(depth.stencilBack),
                stencilReadMask: depth.stencilReadMask, stencilWriteMask: depth.stencilWriteMask, depthBias: depth.depthBias,
                depthBiasSlopeScale: depth.depthBiasSlopeScale, depthBiasClamp: depth.depthBiasClamp,
            } : undefined,
            multisample: { count: multisample.count, mask: multisample.mask, alphaToCoverageEnabled: multisample.alphaToCoverageEnabled },
            fragment: fragment ? {
                ...stage(fragment),
                // A target with an undefined format is a hole (null) in the list.
                targets: memory.array(S.ColorTargetState, fragment.targets, fragment.targetCount).map((t) => (!t.format ? null : {
                    format: E.TextureFormat[t.format], writeMask: t.writeMask,
                    blend: t.blend ? (({ color, alpha }) => ({ color: blendComponent(color), alpha: blendComponent(alpha) }))(memory.read(S.BlendState, t.blend)) : undefined,
                })),
            } : undefined,
        };
    }
    /** Calls a create*PipelineAsync callback: (status, pipeline, message, userdata1, userdata2). */
    const pipelineCallback = (callback, u1, u2, handle, error) => withMessage(error?.message, (message) =>
        context.callPointer(callback, error ? (error.reason === 'internal' ? 4 : 3) : 1, handle ?? 0, message, u1, u2));
    function texelCopyBuffer(pointer) {
        const { layout, buffer } = memory.read(S.TexelCopyBufferInfo, pointer);
        return { buffer: gpu(buffer), offset: layout.offset, bytesPerRow: optional32(layout.bytesPerRow), rowsPerImage: optional32(layout.rowsPerImage) };
    }
    function texelCopyTexture(pointer) {
        const t = memory.read(S.TexelCopyTextureInfo, pointer);
        return { texture: gpu(t.texture), mipLevel: t.mipLevel, origin: t.origin, aspect: E.TextureAspect[t.aspect] };
    }
    const extent = (pointer) => memory.read(S.Extent3D, pointer);
    /** setBindGroup reading the dynamic offsets straight from memory. */
    function setBindGroup(encoder, index, group, count, offsets) {
        if (count) encoder.setBindGroup(index >>> 0, gpu(group) ?? null, context.u32, offsets >>> 2, count >>> 0);
        else encoder.setBindGroup(index >>> 0, gpu(group) ?? null);
    }
    /** setImmediates (WebGPU's immediate data, where the browser has it) reading the bytes straight from memory. */
    const setImmediates = (encoder, offset, data, size) => encoder.setImmediates?.(offset >>> 0, context.bytes, data >>> 0, size >>> 0);

    // ---- Mapped buffers ---------------------------------------------------------------------------------------------
    // A buffer record's `mapping` ({mode, offset, size, bytes, ranges}) describes its current mapping. WebGPU forbids
    // overlapping getMappedRange calls, which webgpu.h allows, so the mapped range is fetched once as `bytes` and
    // every C range is a slice of it (copied into malloc'd memory, `ranges`).
    const mappedBytes = (b) => (b.mapping.bytes ??= new Uint8Array(b.gpu.getMappedRange(b.mapping.offset, b.mapping.size)));
    /** The offset of [offset, offset + size) inside the mapping, or -1 when it is not mapped. */
    function mappedOffset(b, offset, size) {
        const m = b.mapping;
        return m && offset >= m.offset && offset + size <= m.offset + m.size ? offset - m.offset : -1;
    }
    function mappedRange(handle, offset, size, writable) {
        const b = record(handle);
        offset >>>= 0; size >>>= 0;
        if (size === SIZE_MAX) size = Math.max(0, b.gpu.size - offset);
        const at = mappedOffset(b, offset, size);
        if (at < 0) return 0;
        const source = mappedBytes(b), pointer = malloc(size || 1);
        context.bytes.set(source.subarray(at, at + size), pointer);
        b.mapping.ranges.push({ pointer, at, size, writable });
        return pointer;
    }
    /** Ends a mapping on the C side: writes changed ranges back (`keep`, for write mappings) and frees them. */
    function endMapping(b, keep) {
        const m = b.mapping;
        b.mapping = null;
        if (!m) return;
        const bytes = context.bytes;
        for (const r of m.ranges) {
            if (keep && r.writable && (m.mode & MAP_WRITE)) m.bytes.set(bytes.subarray(r.pointer, r.pointer + r.size), r.at);
            free(r.pointer);
        }
    }
    const mapping = (mode, offset, size) => ({ mode, offset, size, bytes: null, ranges: [] });

    // ---- Encoders: the calls render passes, compute passes and render bundles share ---------------------------------
    const debug = (name) => ({
        [`wgpu${name}InsertDebugMarker`]: (h, label) => gpu(h).insertDebugMarker(memory.string(label) ?? ''),
        [`wgpu${name}PushDebugGroup`]: (h, label) => gpu(h).pushDebugGroup(memory.string(label) ?? ''),
        [`wgpu${name}PopDebugGroup`]: (h) => gpu(h).popDebugGroup(),
    });
    const drawing = (name) => ({
        ...debug(name),
        [`wgpu${name}SetPipeline`]: (h, pipeline) => gpu(h).setPipeline(gpu(pipeline)),
        [`wgpu${name}SetBindGroup`]: (h, index, group, count, offsets) => setBindGroup(gpu(h), index, group, count, offsets),
        [`wgpu${name}SetImmediates`]: (h, offset, data, size) => setImmediates(gpu(h), offset, data, size),
        [`wgpu${name}SetVertexBuffer`]: (h, slot, buffer, offset, size) => gpu(h).setVertexBuffer(slot >>> 0, gpu(buffer) ?? null, u64(offset), size64(size)),
        [`wgpu${name}SetIndexBuffer`]: (h, buffer, format, offset, size) => gpu(h).setIndexBuffer(gpu(buffer), E.IndexFormat[format], u64(offset), size64(size)),
        [`wgpu${name}Draw`]: (h, vertices, instances, firstVertex, firstInstance) => gpu(h).draw(vertices >>> 0, instances >>> 0, firstVertex >>> 0, firstInstance >>> 0),
        [`wgpu${name}DrawIndexed`]: (h, indices, instances, firstIndex, baseVertex, firstInstance) =>
            gpu(h).drawIndexed(indices >>> 0, instances >>> 0, firstIndex >>> 0, baseVertex, firstInstance >>> 0),
        [`wgpu${name}DrawIndirect`]: (h, buffer, offset) => gpu(h).drawIndirect(gpu(buffer), u64(offset)),
        [`wgpu${name}DrawIndexedIndirect`]: (h, buffer, offset) => gpu(h).drawIndexedIndirect(gpu(buffer), u64(offset)),
    });

    const functions = {
        // ---- Instance ---------------------------------------------------------------------------------------------
        wgpuCreateInstance() {
            // `ready`: settled AllowProcessEvents operations; `futures`: every operation whose callback has not fired.
            const instance = { gpu: globalThis.navigator?.gpu, refs: 1, ready: [], futures: new Map() };
            instance.instance = instance;
            return context.retain(instance);
        },
        // The web has no instance features: no timed waits (a page cannot block), no SPIR-V, one device per adapter.
        wgpuGetInstanceFeatures: (features) => memory.write(S.SupportedFeatures, features, { featureCount: 0, features: 0 }),
        wgpuGetInstanceLimits: (limits) => { memory.write(S.InstanceLimits, limits, { timedWaitAnyMaxCount: 0 }); return STATUS_SUCCESS; },
        wgpuHasInstanceFeature: () => 0,
        wgpuSupportedInstanceFeaturesFreeMembers: freeFeatures,
        wgpuGetProcAddress: () => 0, // JavaScript functions cannot become C function pointers; call wgpu* directly
        wgpuInstanceGetWGSLLanguageFeatures: (instance, features) => writeFeatures(features, record(instance).gpu?.wgslLanguageFeatures ?? [], E.WGSLLanguageFeatureName),
        wgpuInstanceHasWGSLLanguageFeature: (instance, feature) => (record(instance).gpu?.wgslLanguageFeatures.has(E.WGSLLanguageFeatureName[feature]) ? 1 : 0),
        wgpuSupportedWGSLLanguageFeaturesFreeMembers: freeFeatures,
        wgpuInstanceRequestAdapter(instance, options, info) {
            const r = record(instance), o = options ? memory.read(S.RequestAdapterOptions, options) : {};
            const xr = memory.chain(o.nextInChain).get(SType.RequestAdapterWebXROptions);
            const request = r.gpu ? r.gpu.requestAdapter({
                featureLevel: E.FeatureLevel[o.featureLevel], powerPreference: E.PowerPreference[o.powerPreference],
                forceFallbackAdapter: o.forceFallbackAdapter, xrCompatible: xr ? memory.read(S.RequestAdapterWebXROptions, xr).xrCompatible : undefined,
            }) : Promise.resolve(null);
            return track(r, info, request.then((adapter) => adapter && wrap(adapter, r)), (callback, u1, u2, adapter, error) =>
                withMessage(error?.message ?? (adapter ? '' : 'No WebGPU adapter is available.'), (message) =>
                    context.callPointer(callback, adapter ? 1 : error ? 4 : 3 /* Success, Error, Unavailable */, adapter ?? 0, message, u1, u2)));
        },
        wgpuInstanceProcessEvents(instance) {
            const r = record(instance), ready = r.ready;
            r.ready = [];
            for (const future of ready) fire(future);
        },
        wgpuInstanceWaitAny(instance, count, infos, timeout) {
            if (timeout !== 0n) return WAIT_ERROR; // TimedWaitAny: the page's thread cannot block
            const r = record(instance);
            let completed = false;
            for (let i = 0; i < count >>> 0; i++) {
                const pointer = infos + i * S.FutureWaitInfo.size, id = context.view.getBigUint64(pointer, true);
                const future = r.futures.get(id);
                if (future ? !future.call : id === 0n || id > lastFuture) continue; // still pending, or not a future
                if (future) fire(future);
                memory.write(S.FutureWaitInfo, pointer, { completed: true });
                completed = true;
            }
            return completed ? WAIT_SUCCESS : WAIT_TIMED_OUT;
        },
        wgpuInstanceCreateSurface(instance, descriptor) {
            const d = memory.read(S.Labeled, descriptor);
            const source = memory.chain(d.nextInChain).get(SType.EmscriptenSurfaceSourceCanvasHTMLSelector);
            const canvas = source && document.querySelector(memory.read(S.EmscriptenSurfaceSourceCanvasHTMLSelector, source).selector);
            const surface = canvas?.getContext('webgpu');
            return surface ? wrap(surface, record(instance), { configured: false }) : 0;
        },

        // ---- Adapter ----------------------------------------------------------------------------------------------
        wgpuAdapterGetFeatures: (adapter, features) => writeFeatures(features, gpu(adapter).features, E.FeatureName),
        wgpuAdapterHasFeature: (adapter, feature) => (gpu(adapter).features.has(E.FeatureName[feature]) ? 1 : 0),
        wgpuAdapterGetInfo: (adapter, info) => writeInfo(info, gpu(adapter).info),
        wgpuAdapterGetLimits: (adapter, limits) => writeLimits(limits, gpu(adapter).limits),
        wgpuAdapterInfoFreeMembers: (info) => free(context.view.getUint32(info + S.AdapterInfo.offset.vendor, true)), // the strings' block
        wgpuAdapterRequestDevice(adapter, descriptor, info) {
            const a = record(adapter), instance = a.instance;
            const d = descriptor ? memory.read(S.DeviceDescriptor, descriptor) : null;
            const lost = begin(instance, d?.deviceLostCallbackInfo ?? NO_CALLBACK), errors = d?.uncapturedErrorCallbackInfo ?? NO_CALLBACK;
            const request = a.gpu.requestDevice(d ? {
                label: d.label, defaultQueue: { label: d.defaultQueue.label },
                requiredFeatures: memory.u32s(d.requiredFeatures, d.requiredFeatureCount).map((f) => E.FeatureName[f]).filter(Boolean),
                requiredLimits: requiredLimits(d.requiredLimits, a.gpu.limits),
            } : undefined).then((device) => {
                const handle = wrap(device, instance, { adapter: a, queue: 0, lost: lost.id });
                device.lost.then((reason) => settle(lost, (callback, u1, u2) => withMessage(reason.message, (message, devicePointer) =>
                    context.callPointer(callback, devicePointer, reason.reason === 'destroyed' ? 2 : 1, message, u1, u2), handle)));
                if (errors.callback) device.addEventListener('uncapturederror', (event) => {
                    event.preventDefault();
                    withMessage(event.error.message, (message, devicePointer) =>
                        context.callPointer(errors.callback, devicePointer, errorType(event.error), message, errors.userdata1, errors.userdata2), handle);
                    context.wake();
                });
                return handle;
            }, (error) => {
                settle(lost, (callback, u1, u2) => withMessage(error.message, (message, devicePointer) =>
                    context.callPointer(callback, devicePointer, 4 /* FailedCreation */, message, u1, u2)));
                throw error;
            });
            return track(instance, info, request, (callback, u1, u2, device, error) => withMessage(error?.message, (message) =>
                context.callPointer(callback, device ? 1 : 3 /* Success, Error */, device ?? 0, message, u1, u2)));
        },

        // ---- Device -----------------------------------------------------------------------------------------------
        wgpuDeviceGetFeatures: (device, features) => writeFeatures(features, gpu(device).features, E.FeatureName),
        wgpuDeviceHasFeature: (device, feature) => (gpu(device).features.has(E.FeatureName[feature]) ? 1 : 0),
        wgpuDeviceGetLimits: (device, limits) => writeLimits(limits, gpu(device).limits),
        wgpuDeviceGetAdapterInfo: (device, info) => writeInfo(info, gpu(device).adapterInfo ?? record(device).adapter.gpu.info),
        wgpuDeviceGetLostFuture: (device) => record(device).lost,
        wgpuDeviceDestroy: (device) => gpu(device).destroy(),
        /** The device's one queue keeps one handle, counted by each call. */
        wgpuDeviceGetQueue(device) {
            const d = record(device);
            if (d.queue && context.object(d.queue)?.gpu === d.gpu.queue) { addRef(d.queue); return d.queue; }
            return (d.queue = wrap(d.gpu.queue, d.instance));
        },
        wgpuDevicePushErrorScope: (device, filter) => gpu(device).pushErrorScope(E.ErrorFilter[filter]),
        wgpuDevicePopErrorScope(device, info) {
            const d = record(device);
            return track(d.instance, info, d.gpu.popErrorScope(), (callback, u1, u2, error, failure) => withMessage(failure?.message ?? error?.message, (message) =>
                context.callPointer(callback, failure ? 3 : 1 /* Error, Success */, failure ? 1 : errorType(error), message, u1, u2)));
        },
        wgpuDeviceCreateBuffer(device, descriptor) {
            const d = memory.read(S.BufferDescriptor, descriptor), r = record(device);
            const buffer = r.gpu.createBuffer({ label: d.label, usage: d.usage, size: d.size, mappedAtCreation: d.mappedAtCreation });
            return wrap(buffer, r.instance, { mapping: d.mappedAtCreation ? mapping(MAP_WRITE, 0, d.size) : null });
        },
        wgpuDeviceCreateTexture(device, descriptor) {
            const d = memory.read(S.TextureDescriptor, descriptor), r = record(device);
            const view = memory.chain(d.nextInChain).get(SType.TextureBindingViewDimension);
            return wrap(r.gpu.createTexture({
                label: d.label, usage: d.usage, dimension: E.TextureDimension[d.dimension], size: d.size, format: E.TextureFormat[d.format],
                mipLevelCount: d.mipLevelCount, sampleCount: d.sampleCount, viewFormats: formats(d.viewFormats, d.viewFormatCount),
                textureBindingViewDimension: view ? E.TextureViewDimension[memory.read(S.TextureBindingViewDimension, view).textureBindingViewDimension] : undefined,
            }), r.instance);
        },
        wgpuDeviceCreateSampler(device, descriptor) {
            const r = record(device);
            if (!descriptor) return wrap(r.gpu.createSampler(), r.instance);
            const d = memory.read(S.SamplerDescriptor, descriptor);
            return wrap(r.gpu.createSampler({
                label: d.label, addressModeU: E.AddressMode[d.addressModeU], addressModeV: E.AddressMode[d.addressModeV],
                addressModeW: E.AddressMode[d.addressModeW], magFilter: E.FilterMode[d.magFilter], minFilter: E.FilterMode[d.minFilter],
                mipmapFilter: E.MipmapFilterMode[d.mipmapFilter], lodMinClamp: d.lodMinClamp, lodMaxClamp: d.lodMaxClamp,
                compare: E.CompareFunction[d.compare], maxAnisotropy: d.maxAnisotropy,
            }), r.instance);
        },
        wgpuDeviceCreateShaderModule(device, descriptor) {
            const d = memory.read(S.Labeled, descriptor), r = record(device);
            const wgsl = memory.chain(d.nextInChain).get(SType.ShaderSourceWGSL);
            if (!wgsl) { console.error('webgpu: the web runs WGSL shader modules only (no SPIR-V).'); return 0; }
            return wrap(r.gpu.createShaderModule({ label: d.label, code: memory.read(S.ShaderSourceWGSL, wgsl).code ?? '' }), r.instance);
        },
        wgpuDeviceCreateBindGroupLayout(device, descriptor) {
            const d = memory.read(S.BindGroupLayoutDescriptor, descriptor), r = record(device);
            return wrap(r.gpu.createBindGroupLayout({
                label: d.label,
                entries: memory.array(S.BindGroupLayoutEntry, d.entries, d.entryCount).map((e) => {
                    const entry = { binding: e.binding, visibility: e.visibility };
                    if (e.bindingArraySize > 1) entry.bindingArraySize = e.bindingArraySize;
                    if (e.buffer.type) entry.buffer = { type: E.BufferBindingType[e.buffer.type], hasDynamicOffset: e.buffer.hasDynamicOffset, minBindingSize: e.buffer.minBindingSize };
                    if (e.sampler.type) entry.sampler = { type: E.SamplerBindingType[e.sampler.type] };
                    if (e.texture.sampleType) entry.texture = { sampleType: E.TextureSampleType[e.texture.sampleType], viewDimension: E.TextureViewDimension[e.texture.viewDimension], multisampled: e.texture.multisampled };
                    if (e.storageTexture.access) entry.storageTexture = { access: E.StorageTextureAccess[e.storageTexture.access], format: E.TextureFormat[e.storageTexture.format], viewDimension: E.TextureViewDimension[e.storageTexture.viewDimension] };
                    if (memory.chain(e.nextInChain).has(SType.ExternalTextureBindingLayout)) entry.externalTexture = {};
                    return entry;
                }),
            }), r.instance);
        },
        wgpuDeviceCreateBindGroup(device, descriptor) {
            const d = memory.read(S.BindGroupDescriptor, descriptor), r = record(device);
            return wrap(r.gpu.createBindGroup({
                label: d.label, layout: gpu(d.layout),
                entries: memory.array(S.BindGroupEntry, d.entries, d.entryCount).map((e) => {
                    const external = memory.chain(e.nextInChain).get(SType.ExternalTextureBindingEntry);
                    const resource = e.buffer ? { buffer: gpu(e.buffer), offset: e.offset, size: e.size } : e.sampler ? gpu(e.sampler)
                        : e.textureView ? gpu(e.textureView) : external ? gpu(memory.read(S.ExternalTextureBindingEntry, external).externalTexture) : undefined;
                    return { binding: e.binding, resource };
                }),
            }), r.instance);
        },
        wgpuDeviceCreatePipelineLayout(device, descriptor) {
            const d = memory.read(S.PipelineLayoutDescriptor, descriptor), r = record(device);
            return wrap(r.gpu.createPipelineLayout({
                label: d.label, bindGroupLayouts: gpus(d.bindGroupLayouts, d.bindGroupLayoutCount), immediateSize: d.immediateSize || undefined,
            }), r.instance);
        },
        wgpuDeviceCreateComputePipeline: (device, descriptor) => wrap(gpu(device).createComputePipeline(computePipelineDescriptor(descriptor)), record(device).instance),
        wgpuDeviceCreateRenderPipeline: (device, descriptor) => wrap(gpu(device).createRenderPipeline(renderPipelineDescriptor(descriptor)), record(device).instance),
        wgpuDeviceCreateComputePipelineAsync(device, descriptor, info) {
            const r = record(device);
            return track(r.instance, info, r.gpu.createComputePipelineAsync(computePipelineDescriptor(descriptor)).then((p) => wrap(p, r.instance)), pipelineCallback);
        },
        wgpuDeviceCreateRenderPipelineAsync(device, descriptor, info) {
            const r = record(device);
            return track(r.instance, info, r.gpu.createRenderPipelineAsync(renderPipelineDescriptor(descriptor)).then((p) => wrap(p, r.instance)), pipelineCallback);
        },
        wgpuDeviceCreateCommandEncoder(device, descriptor) {
            const r = record(device);
            return wrap(r.gpu.createCommandEncoder(descriptor ? { label: memory.read(S.Labeled, descriptor).label } : undefined), r.instance);
        },
        wgpuDeviceCreateQuerySet(device, descriptor) {
            const d = memory.read(S.QuerySetDescriptor, descriptor), r = record(device);
            return wrap(r.gpu.createQuerySet({ label: d.label, type: E.QueryType[d.type], count: d.count }), r.instance);
        },
        wgpuDeviceCreateRenderBundleEncoder(device, descriptor) {
            const d = memory.read(S.RenderBundleEncoderDescriptor, descriptor), r = record(device);
            return wrap(r.gpu.createRenderBundleEncoder({
                label: d.label, colorFormats: formats(d.colorFormats, d.colorFormatCount).map((f) => f ?? null),
                depthStencilFormat: E.TextureFormat[d.depthStencilFormat], sampleCount: d.sampleCount,
                depthReadOnly: d.depthReadOnly, stencilReadOnly: d.stencilReadOnly,
            }), r.instance);
        },

        // ---- Queue ------------------------------------------------------------------------------------------------
        wgpuQueueSubmit: (queue, count, commands) => gpu(queue).submit(gpus(commands, count >>> 0)),
        wgpuQueueWriteBuffer: (queue, buffer, offset, data, size) => gpu(queue).writeBuffer(gpu(buffer), u64(offset), context.bytes, data >>> 0, size >>> 0),
        wgpuQueueWriteTexture(queue, destination, data, size, layout, extentPointer) {
            const l = memory.read(S.TexelCopyBufferLayout, layout);
            gpu(queue).writeTexture(texelCopyTexture(destination), context.bytes.subarray(data >>> 0, (data >>> 0) + (size >>> 0)),
                { offset: l.offset, bytesPerRow: optional32(l.bytesPerRow), rowsPerImage: optional32(l.rowsPerImage) }, extent(extentPointer));
        },
        wgpuQueueOnSubmittedWorkDone(queue, info) {
            const q = record(queue);
            return track(q.instance, info, q.gpu.onSubmittedWorkDone(), (callback, u1, u2, value, error) => withMessage(error?.message, (message) =>
                context.callPointer(callback, error ? 3 : 1 /* Error, Success */, message, u1, u2)));
        },

        // ---- Buffer -----------------------------------------------------------------------------------------------
        wgpuBufferGetMappedRange: (buffer, offset, size) => mappedRange(buffer, offset, size, true),
        wgpuBufferGetConstMappedRange: (buffer, offset, size) => mappedRange(buffer, offset, size, false),
        wgpuBufferReadMappedRange(buffer, offset, data, size) {
            const b = record(buffer), at = mappedOffset(b, offset >>> 0, size >>> 0);
            if (at < 0) return STATUS_ERROR;
            context.bytes.set(mappedBytes(b).subarray(at, at + (size >>> 0)), data >>> 0);
            return STATUS_SUCCESS;
        },
        wgpuBufferWriteMappedRange(buffer, offset, data, size) {
            const b = record(buffer), at = mappedOffset(b, offset >>> 0, size >>> 0);
            if (at < 0) return STATUS_ERROR;
            mappedBytes(b).set(context.bytes.subarray(data >>> 0, (data >>> 0) + (size >>> 0)), at);
            return STATUS_SUCCESS;
        },
        wgpuBufferMapAsync(buffer, mode, offset, size, info) {
            const b = record(buffer);
            offset >>>= 0; size >>>= 0;
            const range = mapping(u64(mode), offset, size === SIZE_MAX ? Math.max(0, b.gpu.size - offset) : size);
            const request = b.gpu.mapAsync(range.mode, range.offset, range.size).then(() => { b.mapping = range; });
            return track(b.instance, info, request, (callback, u1, u2, value, error) => withMessage(error?.message, (message) =>
                context.callPointer(callback, !error ? 1 : error.name === 'AbortError' ? 4 : 3 /* Success, Aborted, Error */, message, u1, u2)));
        },
        wgpuBufferUnmap(buffer) { const b = record(buffer); endMapping(b, true); b.gpu.unmap(); },
        wgpuBufferDestroy(buffer) { const b = record(buffer); endMapping(b, false); b.gpu.destroy(); },
        wgpuBufferGetMapState: (buffer) => E.code(E.BufferMapState, gpu(buffer).mapState),
        wgpuBufferGetSize: (buffer) => BigInt(gpu(buffer).size),
        wgpuBufferGetUsage: (buffer) => BigInt(gpu(buffer).usage),

        // ---- Texture ----------------------------------------------------------------------------------------------
        wgpuTextureCreateView(texture, descriptor) {
            const t = record(texture);
            if (!descriptor) return wrap(t.gpu.createView(), t.instance);
            const d = memory.read(S.TextureViewDescriptor, descriptor);
            const swizzle = memory.chain(d.nextInChain).get(SType.TextureComponentSwizzleDescriptor);
            const components = swizzle && memory.read(S.TextureComponentSwizzleDescriptor, swizzle).swizzle;
            return wrap(t.gpu.createView({
                label: d.label, format: E.TextureFormat[d.format], dimension: E.TextureViewDimension[d.dimension], usage: d.usage || undefined,
                aspect: E.TextureAspect[d.aspect], baseMipLevel: d.baseMipLevel, mipLevelCount: optional32(d.mipLevelCount),
                baseArrayLayer: d.baseArrayLayer, arrayLayerCount: optional32(d.arrayLayerCount),
                swizzle: components ? ['r', 'g', 'b', 'a'].map((identity) => E.ComponentSwizzle[components[identity]] ?? identity).join('') : undefined,
            }), t.instance);
        },
        wgpuTextureDestroy: (texture) => gpu(texture).destroy(),
        wgpuTextureGetWidth: (texture) => gpu(texture).width,
        wgpuTextureGetHeight: (texture) => gpu(texture).height,
        wgpuTextureGetDepthOrArrayLayers: (texture) => gpu(texture).depthOrArrayLayers,
        wgpuTextureGetMipLevelCount: (texture) => gpu(texture).mipLevelCount,
        wgpuTextureGetSampleCount: (texture) => gpu(texture).sampleCount,
        wgpuTextureGetDimension: (texture) => E.code(E.TextureDimension, gpu(texture).dimension),
        wgpuTextureGetFormat: (texture) => E.code(E.TextureFormat, gpu(texture).format),
        wgpuTextureGetUsage: (texture) => BigInt(gpu(texture).usage),
        wgpuTextureGetTextureBindingViewDimension: (texture) => E.code(E.TextureViewDimension, gpu(texture).textureBindingViewDimension),

        // ---- Pipelines, shader modules, query sets ------------------------------------------------------------------
        wgpuComputePipelineGetBindGroupLayout: (pipeline, index) => wrap(gpu(pipeline).getBindGroupLayout(index >>> 0), record(pipeline).instance),
        wgpuRenderPipelineGetBindGroupLayout: (pipeline, index) => wrap(gpu(pipeline).getBindGroupLayout(index >>> 0), record(pipeline).instance),
        wgpuShaderModuleGetCompilationInfo(module, info) {
            const m = record(module);
            return track(m.instance, info, m.gpu.getCompilationInfo(), (callback, u1, u2, result, error) => {
                // One block: the WGPUCompilationInfo (16 bytes, so the messages are 8-byte aligned), its
                // WGPUCompilationMessage array, then the message texts. Positions are WebGPU's (UTF-16 units).
                const messages = result?.messages ?? [], texts = messages.map((x) => encoder.encode(x.message));
                const block = malloc(8 + 16 + messages.length * S.CompilationMessage.size + texts.reduce((sum, t) => sum + t.length + 1, 0));
                const compilation = Math.ceil(block / 8) * 8, array = compilation + 16, bytes = context.bytes;
                let text = array + messages.length * S.CompilationMessage.size;
                memory.write(S.CompilationInfo, compilation, { nextInChain: 0, messageCount: messages.length, messages: array });
                messages.forEach((x, i) => {
                    bytes.set(texts[i], text);
                    bytes[text + texts[i].length] = 0;
                    memory.write(S.CompilationMessage, array + i * S.CompilationMessage.size, {
                        nextInChain: 0, message: [text, texts[i].length], type: E.code(E.CompilationMessageType, x.type),
                        lineNum: x.lineNum, linePos: x.linePos, offset: x.offset, length: x.length,
                    });
                    text += texts[i].length + 1;
                });
                try { context.callPointer(callback, error ? 2 /* CallbackCancelled */ : 1, error ? 0 : compilation, u1, u2); } finally { free(block); }
            });
        },
        wgpuQuerySetDestroy: (querySet) => gpu(querySet).destroy(),
        wgpuQuerySetGetCount: (querySet) => gpu(querySet).count,
        wgpuQuerySetGetType: (querySet) => E.code(E.QueryType, gpu(querySet).type),

        // ---- Command encoder --------------------------------------------------------------------------------------
        ...debug('CommandEncoder'),
        wgpuCommandEncoderBeginRenderPass(encoder, descriptor) {
            const e = record(encoder), d = memory.read(S.RenderPassDescriptor, descriptor);
            const maxDrawCount = memory.chain(d.nextInChain).get(SType.RenderPassMaxDrawCount);
            const depth = d.depthStencilAttachment && memory.read(S.RenderPassDepthStencilAttachment, d.depthStencilAttachment);
            return wrap(e.gpu.beginRenderPass({
                label: d.label,
                // An attachment without a view is a hole (null) in the list.
                colorAttachments: memory.array(S.RenderPassColorAttachment, d.colorAttachments, d.colorAttachmentCount).map((a) => (!a.view ? null : {
                    view: gpu(a.view), depthSlice: optional32(a.depthSlice), resolveTarget: gpu(a.resolveTarget),
                    loadOp: E.LoadOp[a.loadOp], storeOp: E.StoreOp[a.storeOp], clearValue: a.clearValue,
                })),
                depthStencilAttachment: depth ? {
                    view: gpu(depth.view), depthLoadOp: E.LoadOp[depth.depthLoadOp], depthStoreOp: E.StoreOp[depth.depthStoreOp],
                    depthClearValue: Number.isNaN(depth.depthClearValue) ? undefined : depth.depthClearValue, depthReadOnly: depth.depthReadOnly,
                    stencilLoadOp: E.LoadOp[depth.stencilLoadOp], stencilStoreOp: E.StoreOp[depth.stencilStoreOp],
                    stencilClearValue: depth.stencilClearValue, stencilReadOnly: depth.stencilReadOnly,
                } : undefined,
                occlusionQuerySet: gpu(d.occlusionQuerySet), timestampWrites: timestampWrites(d.timestampWrites),
                maxDrawCount: maxDrawCount ? memory.read(S.RenderPassMaxDrawCount, maxDrawCount).maxDrawCount : undefined,
            }), e.instance);
        },
        wgpuCommandEncoderBeginComputePass(encoder, descriptor) {
            const e = record(encoder), d = descriptor ? memory.read(S.ComputePassDescriptor, descriptor) : null;
            return wrap(e.gpu.beginComputePass(d ? { label: d.label, timestampWrites: timestampWrites(d.timestampWrites) } : undefined), e.instance);
        },
        wgpuCommandEncoderClearBuffer: (encoder, buffer, offset, size) => gpu(encoder).clearBuffer(gpu(buffer), u64(offset), size64(size)),
        wgpuCommandEncoderCopyBufferToBuffer(encoder, source, sourceOffset, destination, destinationOffset, size) {
            const from = gpu(source);
            gpu(encoder).copyBufferToBuffer(from, u64(sourceOffset), gpu(destination), u64(destinationOffset), size64(size) ?? from.size - u64(sourceOffset));
        },
        wgpuCommandEncoderCopyBufferToTexture: (encoder, source, destination, size) => gpu(encoder).copyBufferToTexture(texelCopyBuffer(source), texelCopyTexture(destination), extent(size)),
        wgpuCommandEncoderCopyTextureToBuffer: (encoder, source, destination, size) => gpu(encoder).copyTextureToBuffer(texelCopyTexture(source), texelCopyBuffer(destination), extent(size)),
        wgpuCommandEncoderCopyTextureToTexture: (encoder, source, destination, size) => gpu(encoder).copyTextureToTexture(texelCopyTexture(source), texelCopyTexture(destination), extent(size)),
        wgpuCommandEncoderResolveQuerySet: (encoder, querySet, first, count, destination, offset) =>
            gpu(encoder).resolveQuerySet(gpu(querySet), first >>> 0, count >>> 0, gpu(destination), u64(offset)),
        wgpuCommandEncoderWriteTimestamp: (encoder, querySet, index) => gpu(encoder).writeTimestamp?.(gpu(querySet), index >>> 0), // removed from WebGPU; kept where browsers still have it
        wgpuCommandEncoderFinish(encoder, descriptor) {
            const e = record(encoder);
            return wrap(e.gpu.finish(descriptor ? { label: memory.read(S.Labeled, descriptor).label } : undefined), e.instance);
        },

        // ---- Compute pass, render pass, render bundle encoder -------------------------------------------------------
        ...debug('ComputePassEncoder'),
        wgpuComputePassEncoderSetPipeline: (pass, pipeline) => gpu(pass).setPipeline(gpu(pipeline)),
        wgpuComputePassEncoderSetBindGroup: (pass, index, group, count, offsets) => setBindGroup(gpu(pass), index, group, count, offsets),
        wgpuComputePassEncoderSetImmediates: (pass, offset, data, size) => setImmediates(gpu(pass), offset, data, size),
        wgpuComputePassEncoderDispatchWorkgroups: (pass, x, y, z) => gpu(pass).dispatchWorkgroups(x >>> 0, y >>> 0, z >>> 0),
        wgpuComputePassEncoderDispatchWorkgroupsIndirect: (pass, buffer, offset) => gpu(pass).dispatchWorkgroupsIndirect(gpu(buffer), u64(offset)),
        wgpuComputePassEncoderEnd: (pass) => gpu(pass).end(),
        ...drawing('RenderPassEncoder'),
        wgpuRenderPassEncoderSetViewport: (pass, x, y, width, height, minDepth, maxDepth) => gpu(pass).setViewport(x, y, width, height, minDepth, maxDepth),
        wgpuRenderPassEncoderSetScissorRect: (pass, x, y, width, height) => gpu(pass).setScissorRect(x >>> 0, y >>> 0, width >>> 0, height >>> 0),
        wgpuRenderPassEncoderSetBlendConstant: (pass, color) => gpu(pass).setBlendConstant(memory.read(S.Color, color)),
        wgpuRenderPassEncoderSetStencilReference: (pass, reference) => gpu(pass).setStencilReference(reference >>> 0),
        wgpuRenderPassEncoderBeginOcclusionQuery: (pass, index) => gpu(pass).beginOcclusionQuery(index >>> 0),
        wgpuRenderPassEncoderEndOcclusionQuery: (pass) => gpu(pass).endOcclusionQuery(),
        wgpuRenderPassEncoderExecuteBundles: (pass, count, bundles) => gpu(pass).executeBundles(gpus(bundles, count >>> 0)),
        wgpuRenderPassEncoderEnd: (pass) => gpu(pass).end(),
        ...drawing('RenderBundleEncoder'),
        wgpuRenderBundleEncoderFinish(encoder, descriptor) {
            const e = record(encoder);
            return wrap(e.gpu.finish(descriptor ? { label: memory.read(S.Labeled, descriptor).label } : undefined), e.instance);
        },

        // ---- Surface (a canvas's GPUCanvasContext) ------------------------------------------------------------------
        wgpuSurfaceGetCapabilities(surface, adapter, capabilities) {
            const preferred = navigator.gpu.getPreferredCanvasFormat();
            const values = [preferred, ...['bgra8unorm', 'rgba8unorm', 'rgba16float'].filter((f) => f !== preferred)].map((f) => E.code(E.TextureFormat, f));
            const presentModes = [1 /* Fifo: the browser presents with the display */], alphaModes = [1 /* Opaque */, 2 /* Premultiplied */];
            const lists = [values, presentModes, alphaModes], block = malloc(4 * lists.flat().length);
            const pointers = [];
            let at = block;
            for (const list of lists) { pointers.push(at); list.forEach((value) => { context.view.setUint32(at, value, true); at += 4; }); }
            memory.write(S.SurfaceCapabilities, capabilities, {
                usages: 0x17 /* CopySrc | CopyDst | TextureBinding | RenderAttachment */, formatCount: values.length, formats: pointers[0],
                presentModeCount: presentModes.length, presentModes: pointers[1], alphaModeCount: alphaModes.length, alphaModes: pointers[2],
            });
            return STATUS_SUCCESS;
        },
        wgpuSurfaceCapabilitiesFreeMembers: (capabilities) => free(memory.read(S.SurfaceCapabilities, capabilities).formats),
        wgpuSurfaceConfigure(surface, configuration) {
            const s = record(surface), c = memory.read(S.SurfaceConfiguration, configuration), canvas = s.gpu.canvas;
            const color = memory.chain(c.nextInChain).get(SType.SurfaceColorManagement);
            const management = color && memory.read(S.SurfaceColorManagement, color);
            // The canvas's pixel size is the surface size.
            if (c.width && canvas.width !== c.width) canvas.width = c.width;
            if (c.height && canvas.height !== c.height) canvas.height = c.height;
            s.gpu.configure({
                device: gpu(c.device), format: E.TextureFormat[c.format], usage: c.usage, viewFormats: formats(c.viewFormats, c.viewFormatCount),
                alphaMode: E.CompositeAlphaMode[c.alphaMode], colorSpace: management ? E.PredefinedColorSpace[management.colorSpace] : undefined,
                toneMapping: management ? { mode: E.ToneMappingMode[management.toneMappingMode] } : undefined,
            });
            s.configured = true;
        },
        wgpuSurfaceUnconfigure(surface) { const s = record(surface); s.gpu.unconfigure(); s.configured = false; },
        wgpuSurfaceGetCurrentTexture(surface, texture) {
            const s = record(surface);
            memory.write(S.SurfaceTexture, texture, s.configured
                ? { texture: wrap(s.gpu.getCurrentTexture(), s.instance), status: 1 /* SuccessOptimal */ }
                : { texture: 0, status: 6 /* Error */ });
        },
        wgpuSurfacePresent: () => STATUS_SUCCESS, // the browser presents the canvas after the frame
        wgpuSurfaceSetLabel: (surface, label) => { record(surface).label = memory.string(label); },
        wgpuSupportedFeaturesFreeMembers: freeFeatures,
    };

    for (const type of OBJECTS) {
        functions[`wgpu${type}AddRef`] = addRef;
        functions[`wgpu${type}Release`] = release;
        if (type !== 'Adapter' && type !== 'Instance') functions[`wgpu${type}SetLabel`] ??= setLabel;
    }
    return functions;
});
