// Audio output on the web: one AudioContext per realtime device, with a minimal AudioWorklet (worklet.mjs) that plays
// the PCM blocks the C mixer renders on the main thread (libraries/audio, output_web.c) and reports each block it
// finished, which paces the mixer. No decoding, mixing or resampling happens here.
//
// Browsers start an AudioContext only after a user gesture (autoplay policy): audio_start resumes the context, and
// while the policy keeps it suspended, the next pointer, key or touch gesture on the page resumes it.
import { registerGlue } from '../core/glue.mjs';

const workletUrl = new URL('./worklet.mjs', import.meta.url);
const gestures = ['pointerdown', 'keydown', 'touchend'];

registerGlue('audio', (context) => {
    class Output {
        constructor(sampleRate, played, data) {
            this.audio = new AudioContext({ sampleRate, latencyHint: 'interactive' });
            this.waiting = [];      // blocks pushed before the worklet is ready
            this.started = false;
            this.closed = false;
            this.abort = new AbortController();
            const report = (frames) => { if (!this.closed) context.callPointer(played, data, frames); };
            this.audio.audioWorklet.addModule(workletUrl).then(() => {
                if (this.closed) return;
                this.node = new AudioWorkletNode(this.audio, 'aquarius-audio', { numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [2] });
                this.node.port.onmessage = (e) => report(e.data);
                this.node.onprocessorerror = () => report(-1);
                this.node.connect(this.audio.destination);
                for (const block of this.waiting) this.node.port.postMessage(block, [block.buffer]);
                this.waiting = null;
            }, () => report(-1));
            const resume = () => { if (this.started && this.audio.state === 'suspended') this.audio.resume().catch(() => {}); };
            for (const type of gestures) document.addEventListener(type, resume, { capture: true, passive: true, signal: this.abort.signal });
        }
        push(block) {
            if (this.waiting) this.waiting.push(block);
            else this.node.port.postMessage(block, [block.buffer]);
        }
    }
    const output = (handle) => context.object(handle);

    return {
        /** A realtime output, or 0 when the page cannot play audio (no AudioWorklet outside a secure context). */
        audio_open(sampleRate, played, data) {
            if (!globalThis.AudioContext || !globalThis.AudioWorkletNode) return 0;
            try { return context.retain(new Output(sampleRate, played, data)); } catch { return 0; }
        },
        audio_start(handle) {
            const o = output(handle);
            o.started = true;
            o.audio.resume().catch(() => {});
        },
        audio_suspend(handle) {
            const o = output(handle);
            o.started = false;
            o.audio.suspend().catch(() => {});
        },
        /** One block of `frames` planar stereo samples (left, then right) at `pointer`. */
        audio_push(handle, pointer, frames) {
            output(handle).push(context.f32.slice(pointer >> 2, (pointer >> 2) + frames * 2));
        },
        audio_close(handle) {
            const o = output(handle);
            o.closed = true;
            o.abort.abort();
            if (o.node) { o.node.port.onmessage = null; o.node.port.postMessage(null); o.node.disconnect(); }
            o.audio.close().catch(() => {});
            context.release(handle);
        },
    };
});
