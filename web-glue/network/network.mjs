// Networking Web APIs for C code (libraries/network/src/web/web.c): fetch with a streamed body and an AbortController,
// WebSocket, and setTimeout. Each function makes one Web API call or starts one operation and reports its events to
// the C callback it was given; requests, limits, deadlines and error mapping are decided in C. A released handle's
// events no longer reach C.
import { registerGlue } from '../core/glue.mjs';

const FETCH_HEAD = 1, FETCH_CHUNK = 2, FETCH_END = 3, FETCH_ERROR = 4;
const SOCKET_OPEN = 1, SOCKET_TEXT = 2, SOCKET_BINARY = 3, SOCKET_CLOSE = 4, SOCKET_ERROR = 5;
const CREDENTIALS = ['omit', 'same-origin', 'include'];
const encoder = new TextEncoder(), decoder = new TextDecoder();

registerGlue('network', (context) => {
    const live = new Set();
    const emit = (handle, callback, data, kind, a = 0, b = 0) => {
        if (!live.has(handle)) return false;
        context.callPointer(callback, data, kind, a, b);
        context.wake();
        return true;
    };
    const copy = (bytes) => context.allocate(bytes)[0];
    // NUL-separated strings, as C reads them.
    const strings = (pointer, length) => {
        const parts = context.text(pointer, length).split('\0');
        if (parts.at(-1) === '') parts.pop();
        return parts;
    };
    const block = (texts) => {
        const encoded = texts.map((t) => encoder.encode(t));
        const length = encoded.reduce((n, e) => n + e.length + 1, 0);
        const bytes = new Uint8Array(4 + length);
        new DataView(bytes.buffer).setUint32(0, length, true);
        let at = 4;
        for (const e of encoded) { bytes.set(e, at); at += e.length + 1; }
        return bytes;
    };

    const openSocket = (url, protocols, callback, data, details = false) => {
        const socket = new WebSocket(context.text(url), protocols);
        socket.binaryType = 'arraybuffer';
        const handle = context.retain(socket);
        live.add(handle);
        socket.addEventListener('open', () => emit(handle, callback, data, SOCKET_OPEN));
        socket.addEventListener('message', (e) => {
            if (!live.has(handle)) return;
            const text = typeof e.data === 'string', bytes = text ? encoder.encode(e.data) : new Uint8Array(e.data);
            emit(handle, callback, data, text ? SOCKET_TEXT : SOCKET_BINARY, copy(bytes), bytes.length);
        });
        socket.addEventListener('close', (e) => emit(handle, callback, data, SOCKET_CLOSE, e.code,
            details ? context.allocate(e.reason)[0] : 0));
        socket.addEventListener('error', () => emit(handle, callback, data, SOCKET_ERROR));
        return handle;
    };

    return {
        network_fetch(method, url, headers, headersLength, body, bodyLength, hasBody, credentials, manual, callback, data) {
            const controller = new AbortController();
            const handle = context.retain(controller);
            live.add(handle);
            const pairs = strings(headers, headersLength);
            const list = [];
            for (let i = 0; i + 1 < pairs.length; i += 2) list.push([pairs[i], pairs[i + 1]]);
            const init = { method: context.text(method), headers: list, credentials: CREDENTIALS[credentials], redirect: manual ? 'manual' : 'follow', signal: controller.signal };
            if (hasBody) init.body = context.bytes.slice(body, body + bodyLength);
            (async () => {
                try {
                    const response = await fetch(context.text(url), init);
                    if (!live.has(handle)) return;
                    const opaque = response.type === 'opaqueredirect';
                    if (!emit(handle, callback, data, FETCH_HEAD, opaque ? -1 : response.status, copy(block([response.url, ...[...response.headers].flat()]))) || opaque) return;
                    const reader = response.body?.getReader();
                    while (reader) {
                        const { value, done } = await reader.read();
                        if (done) break;
                        if (!live.has(handle)) { reader.cancel().catch(() => {}); return; }
                        emit(handle, callback, data, FETCH_CHUNK, copy(value), value.length);
                    }
                    emit(handle, callback, data, FETCH_END);
                } catch (error) {
                    if (live.has(handle)) emit(handle, callback, data, FETCH_ERROR, 0, copy(encoder.encode(String(error?.message ?? error))));
                }
            })();
            return handle;
        },
        network_fetch_abort(handle) { context.object(handle)?.abort?.(); },
        network_release(handle) { live.delete(handle); context.release(handle); },

        network_socket_open(url, callback, data) {
            return openSocket(url, undefined, callback, data);
        },
        network_socket_open_protocols(url, protocols, length, callback, data) {
            try { return openSocket(url, strings(protocols, length), callback, data, true); }
            catch { return 0; }
        },
        network_socket_protocol(handle) { return context.allocate(context.object(handle)?.protocol ?? '')[0]; },
        network_page_host() { return context.allocate(globalThis.location?.hostname ?? '')[0]; },
        network_page_port() { return context.allocate(globalThis.location?.port ?? '')[0]; },
        network_page_secure() { return globalThis.location?.protocol === 'https:' ? 1 : 0; },
        network_socket_send(handle, pointer, length, text) {
            const socket = context.object(handle), bytes = context.bytes.slice(pointer, pointer + length);
            socket.send(text ? decoder.decode(bytes) : bytes);
            return socket.bufferedAmount;
        },
        network_socket_buffered: (handle) => context.object(handle)?.bufferedAmount ?? 0,
        network_socket_close(handle, code, reason) {
            try { context.object(handle)?.close(code, context.text(reason)); } catch { context.object(handle)?.close(); }
        },

        network_timer_start(ms, fire, data) {
            const timer = {};
            const handle = context.retain(timer);
            live.add(handle);
            timer.id = setTimeout(() => {
                if (!live.has(handle)) return;
                context.callPointer(fire, data);
                context.wake();
            }, ms);
            return handle;
        },
        network_timer_cancel(handle) { clearTimeout(context.object(handle)?.id); live.delete(handle); context.release(handle); },
    };
});
