// A semantic DOM mirror (ARIA roles and labels) for applications that draw their own interface on a canvas. The C
// caller validates the nodes; this replaces the mirror's children with one element per node.
import { registerGlue } from '../core/glue.mjs';

registerGlue('accessibility', (context) => {
    let mirror = null;
    return {
        /** nodes: JSON [{id, role, label, value, disabled}]. */
        accessibility_update(nodes, length) {
            if (!mirror) {
                mirror = document.createElement('div');
                mirror.setAttribute('aria-label', 'Aquarius application');
                Object.assign(mirror.style, { position: 'fixed', width: '1px', height: '1px', overflow: 'hidden', clipPath: 'inset(50%)' });
                document.body.append(mirror);
            }
            const fragment = document.createDocumentFragment();
            for (const node of JSON.parse(context.text(nodes, length))) {
                const item = document.createElement('div');
                item.dataset.aquariusId = node.id;
                item.setAttribute('role', node.role);
                item.setAttribute('aria-label', node.label);
                if (node.value !== null && node.value !== undefined) item.setAttribute('aria-valuetext', node.value);
                if (node.disabled) item.setAttribute('aria-disabled', 'true');
                fragment.append(item);
            }
            mirror.replaceChildren(fragment);
        },
        accessibility_close() { mirror?.remove(); mirror = null; },
    };
});
