// Cameras on the web (MediaDevices.getUserMedia), for libraries/camera and any library that needs video input. Each
// open camera is a MediaStream playing in a muted <video>; the C side reads the decoded frame at the video's own size
// into its memory, one call per frame, and does all conversion, scaling, validation and policy itself. Only video is
// requested, never audio. Once C closes a camera, none of its callbacks run any more.
import { registerGlue } from '../core/glue.mjs';

registerGlue('camera', (context) => {
    const media = () => globalThis.navigator?.mediaDevices;
    const settle = (done, data, status, text) => {
        const [pointer, length] = text === undefined ? [0, 0] : context.allocate(text);
        context.callPointer(done, data, status, pointer, length);
        context.wake();
    };
    const failure = (error) => String(error?.name ?? 'Error');

    class Camera {
        constructor() {
            this.closed = false;
            this.stream = null;
            this.video = document.createElement('video');
            this.video.muted = true;
            this.video.playsInline = true;
            this.canvas = null;
            this.context2d = null;
        }
        stop() { for (const track of this.stream?.getTracks() ?? []) track.stop(); }
        decoded() { return this.video.readyState >= 2 && this.video.videoWidth > 0; }
    }

    return {
        camera_supported: () => (globalThis.isSecureContext && media()?.getUserMedia && media()?.enumerateDevices && globalThis.document ? 1 : 0),

        // Video inputs as "id\0label\0..." (labels stay empty until the page holds camera permission).
        camera_list(done, data) {
            media().enumerateDevices().then(
                (devices) => settle(done, data, 0, devices.filter((d) => d.kind === 'videoinput').map((d) => `${d.deviceId}\0${d.label}\0`).join('')),
                (error) => settle(done, data, 1, failure(error)));
        },

        // Starts a camera; `done` runs once its first frame is decoded, or with the DOMException name on failure.
        camera_open(id, length, width, height, fps, done, data) {
            const camera = new Camera(), deviceId = context.text(id, length);
            const handle = context.retain(camera);
            const video = { width: { ideal: width }, height: { ideal: height }, frameRate: { ideal: fps } };
            if (deviceId) video.deviceId = { exact: deviceId };
            media().getUserMedia({ audio: false, video }).then(async (stream) => {
                camera.stream = stream;
                if (camera.closed) { camera.stop(); return; } // closed while the permission prompt was open
                camera.video.srcObject = stream;
                await camera.video.play();
                if (!camera.decoded()) await new Promise((ready) => camera.video.addEventListener('loadeddata', ready, { once: true }));
                if (!camera.closed) settle(done, data, 0);
            }).catch((error) => { if (camera.closed) camera.stop(); else settle(done, data, 1, failure(error)); });
            return handle;
        },

        // The decoded frame's size and media time (which identifies it); 0 while none is decoded.
        camera_frame(handle, size, time) {
            const camera = context.object(handle);
            if (!camera || camera.closed || !camera.decoded()) return 0;
            const view = context.view;
            view.setInt32(size, camera.video.videoWidth, true);
            view.setInt32(size + 4, camera.video.videoHeight, true);
            view.setFloat64(time, camera.video.currentTime, true);
            return 1;
        },

        // Copies the decoded frame's RGBA pixels (width x height, the video's own size) to `pixels`.
        camera_read(handle, pixels, width, height) {
            const camera = context.object(handle);
            if (!camera || camera.closed) return 0;
            if (!camera.canvas || camera.canvas.width !== width || camera.canvas.height !== height) {
                camera.canvas = new OffscreenCanvas(width, height);
                camera.context2d = camera.canvas.getContext('2d', { willReadFrequently: true });
            }
            camera.context2d.drawImage(camera.video, 0, 0, width, height);
            context.bytes.set(camera.context2d.getImageData(0, 0, width, height).data, pixels);
            return 1;
        },

        // Calls `done` when the video presents its next frame.
        camera_wait(handle, done, data) {
            const camera = context.object(handle);
            if (!camera) return;
            const presented = () => { if (!camera.closed) { context.callPointer(done, data); context.wake(); } };
            if (camera.video.requestVideoFrameCallback) camera.video.requestVideoFrameCallback(presented);
            else setTimeout(presented, 16);
        },

        camera_live(handle) {
            const camera = context.object(handle);
            if (!camera || camera.closed) return 0;
            return !camera.stream || camera.stream.getVideoTracks().some((track) => track.readyState === 'live') ? 1 : 0;
        },

        camera_close(handle) {
            const camera = context.object(handle);
            if (!camera) return;
            camera.closed = true;
            camera.stop();
            camera.video.pause();
            camera.video.srcObject = null;
            context.release(handle);
        },
    };
});
