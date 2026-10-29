// Embedded pages (HTMLIFrameElement) for libraries/webframe: create, place over the application's canvas, navigate,
// reload, show and remove. Coordinates are the application's page units from the canvas's top left; what to show and
// where is decided in C.
import { registerGlue } from '../core/glue.mjs';

registerGlue('frame', (context) => {
    const origin = () => globalThis.document?.querySelector('canvas')?.getBoundingClientRect() ?? { left: 0, top: 0 };
    return {
        frame_create(url, length) {
            if (!globalThis.document) return 0;
            const frame = document.createElement('iframe');
            frame.src = context.text(url, length);
            Object.assign(frame.style, { position: 'fixed', border: '0', left: '0px', top: '0px', width: '0px', height: '0px', zIndex: '10', background: 'white' });
            document.body.appendChild(frame);
            return context.retain(frame);
        },
        frame_place(handle, x, y, width, height) {
            const frame = context.object(handle), at = origin();
            if (!frame) return;
            Object.assign(frame.style, { left: `${at.left + x}px`, top: `${at.top + y}px`, width: `${width}px`, height: `${height}px` });
        },
        frame_navigate(handle, url, length) { const frame = context.object(handle); if (frame) frame.src = context.text(url, length); },
        frame_reload(handle) { const frame = context.object(handle); if (frame) frame.src = frame.src; },
        frame_show(handle, visible) { const frame = context.object(handle); if (frame) frame.style.display = visible ? '' : 'none'; },
        frame_remove(handle) { context.object(handle)?.remove(); context.release(handle); },
    };
});
