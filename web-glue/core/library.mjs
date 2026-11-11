// Loads an AquariusLang native library in the page (docs/native-libraries.md): the application .wasm built with
// `aqua build --target web --kind shared` is instantiated with the host core and its libraries like an application, but
// nothing starts; the host's embed runtime runs its top-level code on the first call. The host core reads the module's
// aquarius.exports table and describes it (runtime/embed/web.c); this file only translates: one JavaScript function per
// export, which copies its arguments into memory, calls the embed runtime's aqua_embed_* functions and copies the
// result back. No JavaScript is generated; <name>.d.ts types the object for TypeScript.
import { linkHost, linkLibraries } from './runtime.mjs';

// Type codes: shape | scalar << 8 | struct << 16 (runtime/embed/table.h).
const VOID = 0, STRUCT = 3, ARRAY = 2;
const INT = 1, FLOAT = 2, DOUBLE = 3, BOOL = 4, STRING = 5, LONG = 6;
// aqua_embed_element of each array element scalar, and its typed array.
const ELEMENTS = { [INT]: [0, Int32Array], [LONG]: [1, BigInt64Array], [FLOAT]: [2, Float32Array], [DOUBLE]: [3, Float64Array], [BOOL]: [4, Uint8Array] };

/**
 * @param {{
 *   library: string | URL | ArrayBuffer | Uint8Array,   the library's .wasm (resolved against the page) or its bytes
 *   base: string | URL,                                  where aquarius.wasm, libc.so and libraries/*.wasm are served
 *   libraries?: string[],                                more library side modules to link
 *   output?: (stream: number, line: string) => void,    what the library prints: 1 output, 2 errors
 *   environment?: Object<string, string>,
 * }} options
 * @returns {Promise<Object>} the library: its exports by lowerCamelCase name, abiVersion, initialize() and shutdown(); a
 *   failed call throws an Error with the library's reason
 */
export async function loadLibrary(options) {
    const source = typeof options.library === 'string' ? new URL(options.library, globalThis.location?.href) : options.library;
    const bytes = source instanceof Uint8Array ? source : source instanceof ArrayBuffer ? new Uint8Array(source)
        : new Uint8Array(await (await fetch(source)).arrayBuffer());
    let instance = await open(bytes, options);
    const exported = {
        /** The C ABI version the library implements (the header's <NAME>_ABI_VERSION). */
        abiVersion: instance.host.aqua_web_library_abi_version(),
        initialize() { if (!instance.embed('initialize')) throw instance.failed(); },
        /** Stops the library and resolves once a fresh instance is linked: the page holds one WebAssembly instance per
         * library, so starting again needs a new one. The next call runs the top-level code again. */
        async shutdown() {
            instance.embed('shutdown');
            instance = await open(bytes, options);
        },
    };
    for (const [name, index] of instance.functions) exported[name] = (...args) => instance.call(index, args);
    return exported;
}

/** One instance of the library: the program, the host core and the needed libraries linked into one memory. */
async function open(bytes, options) {
    const { context, host } = await linkHost({ ...options, program: bytes });
    const [module, length] = context.allocate(bytes); // kept for the instance's life
    const failure = host.aqua_web_library_load(module, length);
    if (failure) throw new Error(context.text(failure));
    const natives = Array.from({ length: host.aqua_web_library_native_count() }, (_, i) => context.text(host.aqua_web_library_native_name(i)));
    await linkLibraries(context, host, [...(options.libraries ?? []), ...natives]);

    const embed = (name, ...args) => context.call('aqua_embed_' + name, ...args);
    const failed = () => new Error(context.text(embed('last_error')) || 'The library call failed.');
    const structs = Array.from({ length: host.aqua_web_library_struct_count() }, (_, s) => ({
        name: context.text(host.aqua_web_library_struct_name(s)),
        fields: Array.from({ length: host.aqua_web_library_field_count(s) }, (_, f) => ({
            name: context.text(host.aqua_web_library_field_name(s, f)), type: host.aqua_web_library_field_type(s, f),
        })),
    }));

    // Arguments: each value becomes one aqua_embed_put_* call; a struct is its fields, then the struct.
    const put = (type, value) => {
        const shape = type & 255, scalar = (type >> 8) & 255;
        if (shape === STRUCT) {
            for (const field of structs[type >>> 16].fields) put(field.type, value?.[field.name]);
            embed('put_struct', type >>> 16);
        } else if (shape === ARRAY) {
            const [element, View] = ELEMENTS[scalar];
            const items = View.from(value ?? [], scalar === BOOL ? (v) => (v ? 1 : 0) : scalar === LONG ? BigInt : undefined);
            const pointer = context.call('malloc', Math.max(1, items.byteLength));
            context.bytes.set(new Uint8Array(items.buffer, items.byteOffset, items.byteLength), pointer);
            embed('put_array', pointer, items.length, element);
            context.call('free', pointer);
        } else if (scalar === STRING) {
            if (value === null || value === undefined) { embed('put_string', 0); return; } // the library reports a NULL string
            const [pointer] = context.allocate(String(value));
            embed('put_string', pointer);
            context.call('free', pointer);
        } else if (scalar === LONG) embed('put_long', BigInt(value));
        else if (scalar === INT) embed('put_int', value | 0);
        else if (scalar === FLOAT) embed('put_float', value);
        else if (scalar === DOUBLE) embed('put_double', value);
        else embed('put_bool', value ? 1 : 0);
    };
    // Results: the result (field -1) or a field of a struct result.
    const take = (type, field = -1) => {
        const shape = type & 255, scalar = (type >> 8) & 255;
        if (shape === VOID) return undefined;
        if (shape === STRUCT) {
            const value = {};
            structs[type >>> 16].fields.forEach((f, index) => { value[f.name] = take(f.type, index); });
            return value;
        }
        if (shape === ARRAY) {
            const [element, View] = ELEMENTS[scalar];
            const count = context.call('malloc', 8);
            const data = embed('array', count, element);
            const items = Array.from(new View(context.memory.buffer.slice(data, data + context.u32[count >> 2] * View.BYTES_PER_ELEMENT)));
            context.call('free', data); context.call('free', count);
            return scalar === BOOL ? items.map((v) => v !== 0) : items;
        }
        if (scalar === STRING) {
            const pointer = embed('string', field);
            const text = context.text(pointer);
            context.call('free', pointer);
            return text;
        }
        if (scalar === LONG) return embed('long', field);
        if (scalar === INT) return embed('int', field);
        if (scalar === FLOAT) return embed('float', field);
        if (scalar === DOUBLE) return embed('double', field);
        return embed('bool', field) !== 0;
    };

    const signatures = Array.from({ length: host.aqua_web_library_function_count() }, (_, i) => ({
        parameters: Array.from({ length: host.aqua_web_library_parameter_count(i) }, (_, p) => host.aqua_web_library_parameter_type(i, p)),
        result: host.aqua_web_library_result_type(i),
    }));
    return {
        host, embed, failed,
        functions: signatures.map((_, i) => [context.text(host.aqua_web_library_function_javascript_name(i)), i]),
        call(index, args) {
            const { parameters, result } = signatures[index];
            if (!embed('begin', index)) throw failed();
            parameters.forEach((type, p) => put(type, args[p]));
            const ok = embed('invoke');
            const value = ok ? take(result) : undefined;
            embed('end');
            if (!ok) throw failed();
            return value;
        },
    };
}
