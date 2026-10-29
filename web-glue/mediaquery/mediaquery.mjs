// CSS media queries (window.matchMedia): pointer kind, hover, dark mode, reduced motion and contrast preferences for
// C code that follows the user's settings. Which queries to ask and what they mean is decided in C.
import { registerGlue } from '../core/glue.mjs';

registerGlue('mediaquery', (context) => ({
    /** 1 when the query matches, 0 when not, -1 without matchMedia. */
    mediaquery_matches(query, length) {
        if (!globalThis.matchMedia) return -1;
        return globalThis.matchMedia(context.text(query, length)).matches ? 1 : 0;
    },
}));
