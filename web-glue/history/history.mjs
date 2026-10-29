// The History API for the browser's back button: a guard entry keeps the page while the application handles back,
// and leaving it (popstate) calls the C side, which decides what back means. The decision stays in C
// (libraries/interaction); this only adds the entry, reports its use and goes back when asked.
import { registerGlue } from '../core/glue.mjs';

registerGlue('history', (context) => {
    let listener = null;
    const release = () => { if (listener) globalThis.removeEventListener('popstate', listener); listener = null; };
    return {
        /** Pushes a guard entry; popped(data) runs once when the browser goes back past it. */
        history_guard(popped, data) {
            release();
            globalThis.history.pushState({ aquariusBack: true }, '');
            listener = () => { listener = null; context.callPointer(popped, data); context.wake(); };
            globalThis.addEventListener('popstate', listener, { once: true });
        },
        history_release: release,
        history_back() { globalThis.history.back(); },
    };
});
