// window.print() of one image page: the PNG goes into a hidden frame sized to the printable area, then the browser's
// print dialog opens. The browser owns the dialog; completion or cancellation cannot be observed.
import { registerGlue } from '../core/glue.mjs';

registerGlue('print', (context) => ({
    /** Prints a PNG fitted to the page: callback(data, error). */
    async print_image(png, length, callback, data) {
        const url = URL.createObjectURL(new Blob([context.bytes.slice(png, png + length)], { type: 'image/png' }));
        const frame = document.createElement('iframe');
        frame.title = 'Aquarius print document';
        frame.style.display = 'none';
        let failure = 0;
        try {
            await new Promise((resolve, reject) => {
                frame.onload = resolve;
                frame.onerror = reject;
                frame.srcdoc = `<!doctype html><title>Aquarius document</title><style>@page{margin:12mm}img{max-width:100%;max-height:95vh}</style><img src="${url}" alt="Document">`;
                document.body.append(frame);
            });
            await Promise.all(Array.from(frame.contentDocument.images, (image) => image.decode()));
            frame.contentWindow.focus();
            frame.contentWindow.print();
        } catch (e) {
            failure = context.allocate(`${e?.name ?? 'Error'}: ${e?.message ?? String(e)}`)[0];
        } finally {
            frame.remove();
            URL.revokeObjectURL(url);
        }
        context.callPointer(callback, data, failure);
        context.wake();
    },
}));
