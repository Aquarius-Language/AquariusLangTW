// OpenGL ES calls translated to WebGL 2. Object IDs, pointers and typed arrays are ABI conversions;
// rendering, capability decisions and pixel algorithms belong to the calling C/C++ library.
import { registerGlue } from '../core/glue.mjs';

registerGlue('webgl', context => {
    let current = null;
    const object = id => id ? context.object(id) : null;
    const own = value => {
        if (!value) return 0;
        let id = current.ids.get(value);
        if (!id) { id = context.retain(value); current.ids.set(value, id); current.handles.add(id); }
        return id;
    };
    const release = id => { current.handles.delete(id); context.release(id); };
    const values = (pointer, count, Type = Int32Array) => new Type(context.memory.buffer, pointer, count);
    const pixelView = (pointer, type) => {
        const Type = type === 0x1400 ? Int8Array : type === 0x1402 ? Int16Array :
            type === 0x1406 ? Float32Array : type === 0x1404 ? Int32Array :
            [0x1403, 0x140B, 0x8033, 0x8034, 0x8363].includes(type) ? Uint16Array :
            [0x1405, 0x84FA, 0x8368, 0x8C3B, 0x8C3E].includes(type) ? Uint32Array : Uint8Array;
        return new Type(context.memory.buffer, pointer, Math.floor((context.memory.buffer.byteLength - pointer) / Type.BYTES_PER_ELEMENT));
    };
    const output = (pointer, value, Type = Int32Array) => {
        const result = Array.isArray(value) || ArrayBuffer.isView(value) ? value : [value];
        const destination = values(pointer, result.length, Type);
        result.forEach((v, i) => { destination[i] = typeof v === 'object' && v !== null ? own(v) : Number(v); });
    };
    const log = (text, capacity, length, pointer) => {
        const bytes = new TextEncoder().encode(text || '');
        const n = Math.min(bytes.length, Math.max(0, capacity - 1));
        if (capacity) { context.bytes.set(bytes.subarray(0, n), pointer); context.bytes[pointer + n] = 0; }
        if (length) context.view.setInt32(length, n, true);
    };
    const api = {
        webgl_context_create(canvas) {
            const gl = context.object(canvas).canvas.getContext('webgl2', {
                alpha: false, antialias: false, depth: true, stencil: true, preserveDrawingBuffer: true,
            });
            return gl ? context.retain({ gl, ids: new WeakMap(), handles: new Set(), texts: [], extensions: new Set(), pack: 0, unpack: 0 }) : 0;
        },
        webgl_context_current(handle) { current = handle ? context.object(handle) : null; },
        webgl_context_release(handle) {
            const state = context.object(handle);
            for (const id of state.handles) context.release(id);
            for (const pointer of state.texts) context.call('free', pointer);
            if (current === state) current = null;
            context.release(handle);
        },
        webgl_extension(name) {
            const extension = context.text(name), value = current.gl.getExtension(extension);
            if (value) current.extensions.add(extension);
            return value ? 1 : 0;
        },
        glGetString(name) {
            const result = name === 0x1F03 ? [...current.extensions].map(extension => extension.startsWith('WEBGL_') ? extension : 'GL_' + extension).join(' ') : current.gl.getParameter(name);
            const pointer = context.allocate(String(result ?? ''))[0]; current.texts.push(pointer); return pointer;
        },
        glGetIntegerv(name, pointer) { output(pointer, current.gl.getParameter(name)); },
        glGetFloatv(name, pointer) { output(pointer, current.gl.getParameter(name), Float32Array); },
        glCreateProgram() { return own(current.gl.createProgram()); },
        glCreateShader(type) { return own(current.gl.createShader(type)); },
        glDeleteProgram(id) { current.gl.deleteProgram(object(id)); release(id); },
        glDeleteShader(id) { current.gl.deleteShader(object(id)); release(id); },
        glShaderSource(shader, count, strings, lengths) {
            const parts = [];
            for (let i = 0; i < count; i++) parts.push(context.text(context.view.getUint32(strings + i * 4, true), lengths ? context.view.getInt32(lengths + i * 4, true) : -1));
            current.gl.shaderSource(object(shader), parts.join(''));
        },
        glCompileShader(shader) { current.gl.compileShader(object(shader)); },
        glAttachShader(program, shader) { current.gl.attachShader(object(program), object(shader)); },
        glDetachShader(program, shader) { current.gl.detachShader(object(program), object(shader)); },
        glLinkProgram(program) { current.gl.linkProgram(object(program)); },
        glUseProgram(program) { current.gl.useProgram(object(program)); },
        glBindAttribLocation(program, index, name) { current.gl.bindAttribLocation(object(program), index, context.text(name)); },
        glGetProgramiv(program, name, pointer) { output(pointer, name === 0x8B84 ? new TextEncoder().encode(current.gl.getProgramInfoLog(object(program)) || '').length + 1 : current.gl.getProgramParameter(object(program), name)); },
        glGetShaderiv(shader, name, pointer) { output(pointer, name === 0x8B84 ? new TextEncoder().encode(current.gl.getShaderInfoLog(object(shader)) || '').length + 1 : current.gl.getShaderParameter(object(shader), name)); },
        glGetShaderInfoLog(shader, capacity, length, pointer) { log(current.gl.getShaderInfoLog(object(shader)), capacity, length, pointer); },
        glGetProgramInfoLog(program, capacity, length, pointer) { log(current.gl.getProgramInfoLog(object(program)), capacity, length, pointer); },
        glGetUniformBlockIndex(program, name) { return current.gl.getUniformBlockIndex(object(program), context.text(name)); },
        glUniformBlockBinding(program, block, binding) { current.gl.uniformBlockBinding(object(program), block, binding); },
        glGetUniformLocation(program, name) { const result = current.gl.getUniformLocation(object(program), context.text(name)); return result ? own(result) : -1; },
        glUniform1i(location, value) { current.gl.uniform1i(location < 0 ? null : object(location), value); },
        glUniform1ui(location, value) { current.gl.uniform1ui(location < 0 ? null : object(location), value >>> 0); },
        glUniform1f(location, value) { current.gl.uniform1f(location < 0 ? null : object(location), value); },
        glBindBuffer(target, id) { current.gl.bindBuffer(target, object(id)); if (target === 0x88EB) current.pack = id; if (target === 0x88EC) current.unpack = id; },
        glBindBufferBase(target, index, id) { current.gl.bindBufferBase(target, index, object(id)); },
        glBindBufferRange(target, index, id, offset, size) { current.gl.bindBufferRange(target, index, object(id), offset, size); },
        glBufferData(target, size, pointer, usage) { current.gl.bufferData(target, pointer ? context.bytes.subarray(pointer, pointer + size) : size, usage); },
        glBufferSubData(target, offset, size, pointer) { current.gl.bufferSubData(target, offset, context.bytes.subarray(pointer, pointer + size)); },
        glGetBufferSubData(target, offset, size, pointer) { current.gl.getBufferSubData(target, offset, context.bytes.subarray(pointer, pointer + size)); },
        glBindTexture(target, id) { current.gl.bindTexture(target, object(id)); },
        glBindSampler(unit, id) { current.gl.bindSampler(unit, object(id)); },
        glSamplerParameteri(id, name, value) { current.gl.samplerParameteri(object(id), name, value); },
        glSamplerParameterf(id, name, value) { current.gl.samplerParameterf(object(id), name, value); },
        glBindFramebuffer(target, id) { current.gl.bindFramebuffer(target, object(id)); },
        glBindRenderbuffer(target, id) { current.gl.bindRenderbuffer(target, object(id)); },
        glFramebufferTexture2D(target, attachment, textureTarget, id, level) { current.gl.framebufferTexture2D(target, attachment, textureTarget, object(id), level); },
        glFramebufferTextureLayer(target, attachment, id, level, layer) { current.gl.framebufferTextureLayer(target, attachment, object(id), level, layer); },
        glFramebufferRenderbuffer(target, attachment, bufferTarget, id) { current.gl.framebufferRenderbuffer(target, attachment, bufferTarget, object(id)); },
        glBindVertexArray(id) { current.gl.bindVertexArray(object(id)); },
        glFenceSync(condition, flags) { return own(current.gl.fenceSync(condition, flags)); },
        glClientWaitSync(id, flags, timeout) { return current.gl.clientWaitSync(object(id), flags, Number(timeout)); },
        glDeleteSync(id) { current.gl.deleteSync(object(id)); release(id); },
        glBeginQuery(target, id) { current.gl.beginQuery(target, object(id)); },
        glGetQueryObjectuiv(id, name, pointer) { output(pointer, current.gl.getQueryParameter(object(id), name), Uint32Array); },
        glDrawBuffers(count, pointer) { current.gl.drawBuffers(values(pointer, count, Uint32Array)); },
        glInvalidateFramebuffer(target, count, pointer) { current.gl.invalidateFramebuffer(target, values(pointer, count, Uint32Array)); },
        glClearBufferfv(buffer, draw, pointer) { current.gl.clearBufferfv(buffer, draw, values(pointer, buffer === 0x1800 ? 4 : 1, Float32Array)); },
        glClearBufferiv(buffer, draw, pointer) { current.gl.clearBufferiv(buffer, draw, values(pointer, buffer === 0x1800 ? 4 : 1)); },
        glClearBufferuiv(buffer, draw, pointer) { current.gl.clearBufferuiv(buffer, draw, values(pointer, 4, Uint32Array)); },
        glTexImage2D(target, level, internal, width, height, border, format, type, pointer) {
            current.gl.texImage2D(target, level, internal, width, height, border, format, type, current.unpack ? pointer : pointer ? pixelView(pointer, type) : null);
        },
        glTexSubImage2D(target, level, x, y, width, height, format, type, pointer) {
            current.gl.texSubImage2D(target, level, x, y, width, height, format, type, current.unpack ? pointer : pixelView(pointer, type));
        },
        glTexSubImage3D(target, level, x, y, z, width, height, depth, format, type, pointer) {
            current.gl.texSubImage3D(target, level, x, y, z, width, height, depth, format, type, current.unpack ? pointer : pixelView(pointer, type));
        },
        glCompressedTexSubImage2D(target, level, x, y, width, height, format, size, pointer) {
            if (current.unpack) current.gl.compressedTexSubImage2D(target, level, x, y, width, height, format, size, pointer);
            else current.gl.compressedTexSubImage2D(target, level, x, y, width, height, format, context.bytes.subarray(pointer, pointer + size));
        },
        glCompressedTexSubImage3D(target, level, x, y, z, width, height, depth, format, size, pointer) {
            if (current.unpack) current.gl.compressedTexSubImage3D(target, level, x, y, z, width, height, depth, format, size, pointer);
            else current.gl.compressedTexSubImage3D(target, level, x, y, z, width, height, depth, format, context.bytes.subarray(pointer, pointer + size));
        },
        glReadPixels(x, y, width, height, format, type, pointer) { current.gl.readPixels(x, y, width, height, format, type, current.pack ? pointer : pixelView(pointer, type)); },
    };
    // A scalar GL call maps directly to the corresponding WebGL method, without any library behavior.
    const scalar = ['getError', 'disable', 'enable', 'depthFunc', 'flush', 'finish', 'hint', 'frontFace', 'cullFace',
        'blendEquationSeparate', 'blendFuncSeparate', 'colorMask', 'depthMask', 'stencilFuncSeparate', 'stencilOpSeparate',
        'stencilMaskSeparate', 'polygonOffset', 'activeTexture', 'pixelStorei', 'scissor', 'viewport', 'texParameteri',
        'texParameterf', 'generateMipmap', 'copyBufferSubData', 'blitFramebuffer', 'drawElementsInstanced', 'drawArraysInstanced',
        'texStorage2D', 'texStorage3D', 'renderbufferStorageMultisample', 'renderbufferStorage', 'vertexAttribIPointer',
        'vertexAttribPointer', 'vertexAttribI4ui', 'vertexAttrib4f', 'enableVertexAttribArray', 'disableVertexAttribArray',
        'clearBufferfi', 'clearColor', 'clearStencil', 'clear', 'drawElements', 'drawArrays', 'endQuery'];
    for (const method of scalar) api['gl' + method[0].toUpperCase() + method.slice(1)] = (...args) => current.gl[method](...args);
    api.glDepthRangef = (...args) => current.gl.depthRange(...args);
    api.glClearDepthf = value => current.gl.clearDepth(value);
    for (const singular of ['Buffer', 'Sampler', 'Texture', 'Framebuffer', 'Renderbuffer', 'Query', 'VertexArray']) {
        api['webgl_create_' + singular.toLowerCase()] = () => own(current.gl['create' + singular]());
        api['webgl_delete_' + singular.toLowerCase()] = id => {
            current.gl['delete' + singular](object(id)); release(id);
            if (singular === 'Buffer') { if (current.pack === id) current.pack = 0; if (current.unpack === id) current.unpack = 0; }
        };
    }
    for (const components of [1, 2, 3, 4]) for (const [suffix, Type] of [['fv', Float32Array], ['iv', Int32Array], ['uiv', Uint32Array]]) {
        const method = 'uniform' + components + suffix;
        api['glU' + method.slice(1)] = (location, count, pointer) => current.gl[method](location < 0 ? null : object(location), values(pointer, count * components, Type));
    }
    for (const dimension of [2, 3, 4]) {
        const method = 'uniformMatrix' + dimension + 'fv';
        api['glU' + method.slice(1)] = (location, count, transpose, pointer) => current.gl[method](location < 0 ? null : object(location), !!transpose, values(pointer, count * dimension * dimension, Float32Array));
    }
    return api;
});
