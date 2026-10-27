// RTCPeerConnection and RTCDataChannel Web APIs. C owns negotiation policy, objects, events and message limits.
import { registerGlue } from '../core/glue.mjs';

const encoder = new TextEncoder(), decoder = new TextDecoder();
registerGlue('webrtc', context => {
    const emit = (handle, object, callback, data, kind, a = 0, b = 0, c = 0) => {
        if (context.object(handle) !== object) return;
        context.callPointer(callback, data, kind, a, b, c);
        context.wake();
    };
    const text = value => context.allocate(value ?? '')[0];
    const complete = (callback, data, ok, value = '') => {
        context.callPointer(callback, data, ok, text(value));
        context.wake();
    };
    const operation = (handle, callback, data, start) => {
        const object = context.object(handle);
        Promise.resolve().then(() => start(object)).then(
            value => complete(callback, data, 1, value),
            error => complete(callback, data, 0, String(error?.message ?? error)));
    };
    return {
        webrtc_peer_create(servers, count, callback, data) {
            const iceServers = [];
            for (let i = 0; i < count; i++) {
                const at = servers + i * 12;
                iceServers.push({ urls: context.text(context.view.getUint32(at, true)),
                    username: context.text(context.view.getUint32(at + 4, true)),
                    credential: context.text(context.view.getUint32(at + 8, true)) });
            }
            try {
                const pc = new RTCPeerConnection({ iceServers }), handle = context.retain(pc);
                pc.addEventListener('connectionstatechange', () => {
                    if (context.object(handle) === pc) emit(handle, pc, callback, data, 1, text(pc.connectionState));
                });
                pc.addEventListener('icecandidate', event => {
                    if (context.object(handle) !== pc) return;
                    if (event.candidate) emit(handle, pc, callback, data, 2,
                        text(event.candidate.candidate), text(event.candidate.sdpMid), event.candidate.sdpMLineIndex ?? 0);
                    else emit(handle, pc, callback, data, 3);
                });
                pc.addEventListener('datachannel', event => {
                    if (context.object(handle) === pc) emit(handle, pc, callback, data, 4, context.retain(event.channel));
                });
                return handle;
            } catch { return 0; }
        },
        webrtc_offer(handle, callback, data) {
            operation(handle, callback, data, pc => pc.createOffer().then(value => value.sdp));
        },
        webrtc_answer(handle, callback, data) {
            operation(handle, callback, data, pc => pc.createAnswer().then(value => value.sdp));
        },
        webrtc_local_set(handle, type, sdp, callback, data) {
            const description = { type: context.text(type), sdp: context.text(sdp) };
            operation(handle, callback, data, pc => pc.setLocalDescription(description));
        },
        webrtc_remote_set(handle, type, sdp, callback, data) {
            const description = { type: context.text(type), sdp: context.text(sdp) };
            operation(handle, callback, data, pc => pc.setRemoteDescription(description));
        },
        webrtc_candidate_add(handle, candidate, mid, line, callback, data) {
            const value = { candidate: context.text(candidate), sdpMid: context.text(mid), sdpMLineIndex: line };
            operation(handle, callback, data, pc => pc.addIceCandidate(value));
        },
        webrtc_peer_close(handle) { context.object(handle)?.close(); },
        webrtc_channel_create(handle, label, protocol, ordered, negotiated, id) {
            try { return context.retain(context.object(handle).createDataChannel(context.text(label),
                { protocol: context.text(protocol), ordered: !!ordered, negotiated: !!negotiated,
                    ...(negotiated ? { id } : {}) })); }
            catch { return 0; }
        },
        webrtc_channel_listen(handle, callback, data) {
            const dc = context.object(handle);
            dc.binaryType = 'arraybuffer';
            dc.addEventListener('open', () => emit(handle, dc, callback, data, 1));
            dc.addEventListener('message', event => {
                if (context.object(handle) !== dc) return;
                const isText = typeof event.data === 'string';
                const bytes = isText ? encoder.encode(event.data) : new Uint8Array(event.data);
                emit(handle, dc, callback, data, isText ? 2 : 3, context.allocate(bytes)[0], bytes.length);
            });
            dc.addEventListener('close', () => emit(handle, dc, callback, data, 4));
            dc.addEventListener('error', event => {
                if (context.object(handle) === dc) emit(handle, dc, callback, data, 5,
                    text(event.error?.message ?? 'data channel error'), event.error?.sctpCauseCode ?? 0);
            });
        },
        webrtc_channel_label: handle => text(context.object(handle)?.label),
        webrtc_channel_protocol: handle => text(context.object(handle)?.protocol),
        webrtc_channel_id: handle => context.object(handle)?.id ?? -1,
        webrtc_channel_ordered: handle => context.object(handle)?.ordered ? 1 : 0,
        webrtc_channel_state: handle => text(context.object(handle)?.readyState ?? 'closed'),
        webrtc_channel_buffered: handle => context.object(handle)?.bufferedAmount ?? 0,
        webrtc_channel_send(handle, pointer, length, isText) {
            try {
                const bytes = context.bytes.slice(pointer, pointer + length);
                context.object(handle).send(isText ? decoder.decode(bytes) : bytes);
                return 1;
            } catch { return 0; }
        },
        webrtc_channel_close(handle) { context.object(handle)?.close(); },
        webrtc_release(handle) { context.release(handle); },
    };
});
