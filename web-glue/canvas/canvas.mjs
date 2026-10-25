// The Canvas 2D API for text: measuring a line, drawing it into an alpha mask, listing installed fonts
// (queryLocalFonts) and the page's leave confirmation (beforeunload). Fonts, layout, caching and availability are
// decided in C (libraries/graphics/src/platform/web.c); this file only calls the Web APIs.
import { registerGlue } from '../core/glue.mjs';

registerGlue('canvas', (context) => {
    let measuring = null;
    const canvas2d = (width, height) => {
        const canvas = typeof OffscreenCanvas === 'function' ? new OffscreenCanvas(width, height) : Object.assign(document.createElement('canvas'), { width, height });
        return canvas.getContext('2d', { willReadFrequently: true });
    };
    let guarded = false;
    const guard = (e) => { e.preventDefault(); e.returnValue = ''; };
    return {
        /** Writes advance width, font ascent and font descent (doubles) at `metrics`; 0 without a 2D context. */
        canvas_measure_text(font, fontLength, text, textLength, metrics) {
            measuring ??= canvas2d(1, 1);
            if (!measuring) return 0;
            measuring.font = context.text(font, fontLength);
            const m = measuring.measureText(context.text(text, textLength));
            const view = context.view;
            view.setFloat64(metrics, m.width, true);
            view.setFloat64(metrics + 8, m.fontBoundingBoxAscent ?? m.actualBoundingBoxAscent, true);
            view.setFloat64(metrics + 16, m.fontBoundingBoxDescent ?? m.actualBoundingBoxDescent, true);
            return 1;
        },
        /** Draws the text white at (x, baseline) into a width x height canvas and copies its alpha to `alpha`. */
        canvas_draw_text(font, fontLength, text, textLength, width, height, x, baseline, alpha) {
            const c = canvas2d(width, height);
            if (!c) return 0;
            c.font = context.text(font, fontLength);
            c.fillStyle = '#fff';
            c.textBaseline = 'alphabetic';
            c.fillText(context.text(text, textLength), x, baseline);
            const rgba = c.getImageData(0, 0, width, height).data, bytes = context.bytes;
            for (let i = 0, n = width * height; i < n; i++) bytes[alpha + i] = rgba[i * 4 + 3];
            return 1;
        },
        /** Lists installed faces as "family\tstyle\n..." text (malloc'd) through `done(data, text, length, ok)`. */
        canvas_query_fonts(done, data) {
            const settle = (text, ok) => { const [pointer, length] = context.allocate(text); context.callPointer(done, data, pointer, length, ok); context.wake(); };
            if (typeof globalThis.queryLocalFonts !== 'function') { queueMicrotask(() => settle('Local font enumeration unavailable', 0)); return; }
            globalThis.queryLocalFonts().then(
                (fonts) => settle(fonts.map((f) => `${f.family}\t${f.style}`).join('\n'), 1),
                (e) => settle(String(e?.message ?? e), 0));
        },
        /** While enabled, leaving the page asks the person to confirm. */
        canvas_close_guard(enabled) {
            if (!!enabled === guarded) return;
            guarded = !!enabled;
            if (guarded) addEventListener('beforeunload', guard); else removeEventListener('beforeunload', guard);
        },
    };
});
