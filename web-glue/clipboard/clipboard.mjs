// The asynchronous Clipboard API (navigator.clipboard): text, and PNG images through ClipboardItem. The browser asks
// for permission and requires a focused secure page; errors return as "Name: message" texts to the C caller.
import { registerGlue } from '../core/glue.mjs';

registerGlue('clipboard', (context) => {
    const error = (e) => context.allocate(`${e?.name ?? 'Error'}: ${e?.message ?? String(e)}`)[0];
    const clipboard = () => globalThis.navigator?.clipboard;
    return {
        /** 1 clipboard, 2 read(), 4 write(), 8 ClipboardItem, 16 ClipboardItem supports image/png. */
        clipboard_features: () => {
            const c = clipboard(), item = globalThis.ClipboardItem;
            return (c ? 1 : 0) | (c?.read ? 2 : 0) | (c?.write ? 4 : 0) | (item ? 8 : 0) | (item && (!item.supports || item.supports('image/png')) ? 16 : 0);
        },
        /** callback(data, text, length, hasText, png, pngLength, error): readText() when read() is missing. */
        async clipboard_read(withImage, callback, data) {
            try {
                let text = null, png = null;
                if (!clipboard().read) text = await clipboard().readText();
                else {
                    for (const item of await clipboard().read()) {
                        if (text === null && item.types.includes('text/plain')) text = await (await item.getType('text/plain')).text();
                        if (withImage && png === null && item.types.includes('image/png'))
                            png = new Uint8Array(await (await item.getType('image/png')).arrayBuffer());
                    }
                }
                const [t, tl] = text === null ? [0, 0] : context.allocate(text);
                const [p, pl] = png === null ? [0, 0] : context.allocate(png);
                context.callPointer(callback, data, t, tl, text === null ? 0 : 1, p, pl, 0);
            } catch (e) { context.callPointer(callback, data, 0, 0, 0, 0, 0, error(e)); }
            context.wake();
        },
        /** callback(data, types, error): the clipboard items' types, JSON. */
        async clipboard_types(callback, data) {
            try {
                const items = await clipboard().read();
                context.callPointer(callback, data, context.allocate(JSON.stringify([...new Set(items.flatMap((i) => i.types))]))[0], 0);
            } catch (e) { context.callPointer(callback, data, 0, error(e)); }
            context.wake();
        },
        /** Writes text, a PNG, or both as one item: callback(data, error). */
        async clipboard_write(text, length, hasText, png, pngLength, callback, data) {
            const value = hasText ? context.text(text, length) : null;
            const image = pngLength ? context.bytes.slice(png, png + pngLength) : null;
            try {
                if (!image) await clipboard().writeText(value);
                else {
                    const representations = { 'image/png': new Blob([image], { type: 'image/png' }) };
                    if (value !== null) representations['text/plain'] = new Blob([value], { type: 'text/plain' });
                    await clipboard().write([new ClipboardItem(representations)]);
                }
                context.callPointer(callback, data, 0);
            } catch (e) { context.callPointer(callback, data, error(e)); }
            context.wake();
        },
    };
});
