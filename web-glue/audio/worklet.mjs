// The AudioWorklet half of web-glue/audio: plays the planar stereo blocks the C mixer posts, in order, and posts back
// the frame count of each block it finished. Plays silence when nothing is queued; a null message ends the processor.
class AquariusAudio extends AudioWorkletProcessor {
    constructor() {
        super();
        this.blocks = [];
        this.offset = 0;
        this.open = true;
        this.port.onmessage = (e) => { if (e.data) this.blocks.push(e.data); else this.open = false; };
    }
    process(inputs, outputs) {
        const [left, right] = outputs[0];
        let written = 0;
        while (written < left.length && this.blocks.length) {
            const block = this.blocks[0], frames = block.length >> 1;
            const count = Math.min(left.length - written, frames - this.offset);
            left.set(block.subarray(this.offset, this.offset + count), written);
            right?.set(block.subarray(frames + this.offset, frames + this.offset + count), written);
            written += count;
            this.offset += count;
            if (this.offset === frames) { this.blocks.shift(); this.offset = 0; this.port.postMessage(frames); }
        }
        return this.open;
    }
}
registerProcessor('aquarius-audio', AquariusAudio);
