// Files the user picks or the browser launches the page with: the File System Access API (open, save and directory
// pickers, writable handles, directory entries), <input type="file"> and downloads. Picked files and handles cross to
// C as glue handles; lists come back as JSON text [{handle, name, kind}] (kind 0 file, 1 directory). Errors are
// "Name: message" texts (AbortError when the user dismissed a picker); the C caller decides what they mean.
import { registerGlue } from '../core/glue.mjs';

registerGlue('files', (context) => {
    let launched = [];
    globalThis.launchQueue?.setConsumer?.((parameters) => { launched = [...(parameters.files ?? [])]; });
    const text = (value) => (value === undefined ? 0 : context.allocate(typeof value === 'string' ? value : JSON.stringify(value))[0]);
    const error = (e) => (e ? context.allocate(`${e.name ?? 'Error'}: ${e.message ?? String(e)}`)[0] : 0);
    const reply = (callback, data, value, failure) => { context.callPointer(callback, data, text(value), error(failure)); context.wake(); };
    const entry = (object, name, kind) => ({ handle: context.retain(object), name, kind });
    const fileOf = async (object) => (typeof object.getFile === 'function' ? await object.getFile() : object);

    return {
        /** 1 open, 2 save, 4 directory pickers, 8 launchQueue, 16 secure context, 32 focused, 64 print, 128 local fonts. */
        files_features: () => (globalThis.showOpenFilePicker ? 1 : 0) | (globalThis.showSaveFilePicker ? 2 : 0) |
            (globalThis.showDirectoryPicker ? 4 : 0) | (globalThis.launchQueue ? 8 : 0) | (globalThis.isSecureContext ? 16 : 0) |
            (globalThis.document?.hasFocus?.() ? 32 : 0) | (globalThis.print ? 64 : 0) | (globalThis.queryLocalFonts ? 128 : 0),

        async files_pick_open(multiple, types, typesLength, callback, data) {
            try {
                const handles = await showOpenFilePicker({ multiple: !!multiple, types: JSON.parse(context.text(types, typesLength)) });
                reply(callback, data, handles.map((h) => entry(h, h.name, 0)));
            } catch (e) { reply(callback, data, undefined, e); }
        },
        files_pick_input(multiple, accept, acceptLength, capture, callback, data) {
            const input = document.createElement('input');
            input.type = 'file';
            input.multiple = !!multiple;
            input.accept = context.text(accept, acceptLength);
            if (capture) input.setAttribute('capture', 'environment');
            input.style.display = 'none';
            document.body.append(input);
            let settled = false;
            const finish = (files) => {
                if (settled) return;
                settled = true;
                input.remove();
                reply(callback, data, files.map((f) => entry(f, f.name, 0)));
            };
            input.addEventListener('change', () => finish(Array.from(input.files ?? [])), { once: true });
            input.addEventListener('cancel', () => finish([]), { once: true });
            input.click();
        },
        async files_pick_save(suggested, suggestedLength, types, typesLength, callback, data) {
            try {
                const handle = await showSaveFilePicker({ suggestedName: context.text(suggested, suggestedLength), types: JSON.parse(context.text(types, typesLength)) });
                reply(callback, data, [entry(handle, handle.name, 0)]);
            } catch (e) { reply(callback, data, undefined, e); }
        },
        async files_pick_directory(callback, data) {
            try {
                const handle = await showDirectoryPicker();
                reply(callback, data, [entry(handle, handle.name, 1)]);
            } catch (e) { reply(callback, data, undefined, e); }
        },
        files_launch(callback, data) { reply(callback, data, launched.map((h) => entry(h, h.name, h.kind === 'directory' ? 1 : 0))); },

        /** {kind, size, modified} of a picked file or handle (size and modified null for directories). */
        async files_info(handle, callback, data) {
            try {
                const object = context.object(handle);
                if (object.kind === 'directory') { reply(callback, data, { kind: 1, size: null, modified: null }); return; }
                const file = await fileOf(object);
                reply(callback, data, { kind: 0, size: file.size, modified: file.lastModified ?? null });
            } catch (e) { reply(callback, data, undefined, e); }
        },
        /** callback(data, bytes, length, error) with bytes from malloc. */
        async files_read(handle, callback, data) {
            try {
                const file = await fileOf(context.object(handle));
                const [pointer, length] = context.allocate(new Uint8Array(await file.arrayBuffer()));
                context.callPointer(callback, data, pointer, length, 0);
            } catch (e) { context.callPointer(callback, data, 0, 0, error(e)); }
            context.wake();
        },
        files_writable: (handle) => (typeof context.object(handle)?.createWritable === 'function' ? 1 : 0),
        async files_write(handle, bytes, length, callback, data) {
            const content = context.bytes.slice(bytes, bytes + length);
            let writer;
            try {
                writer = await context.object(handle).createWritable();
                await writer.write(content);
                await writer.close();
                context.callPointer(callback, data, 0);
            } catch (e) {
                await writer?.abort?.().catch?.(() => {});
                context.callPointer(callback, data, error(e));
            }
            context.wake();
        },
        async files_entries(handle, callback, data) {
            try {
                const list = [];
                for await (const [name, child] of context.object(handle).entries()) {
                    list.push(entry(child, name, child.kind === 'directory' ? 1 : 0));
                    if (list.length > 4096) break;
                }
                reply(callback, data, list);
            } catch (e) { reply(callback, data, undefined, e); }
        },
        async files_remove(handle, recursive, callback, data) {
            try {
                const object = context.object(handle);
                if (typeof object.remove !== 'function') throw { name: 'NotSupportedError', message: 'Browser cannot delete selected files' };
                await object.remove({ recursive: !!recursive });
                context.callPointer(callback, data, 0);
            } catch (e) { context.callPointer(callback, data, error(e)); }
            context.wake();
        },
        files_download(name, nameLength, bytes, length) {
            const url = URL.createObjectURL(new Blob([context.bytes.slice(bytes, bytes + length)]));
            const link = document.createElement('a');
            link.href = url;
            link.download = context.text(name, nameLength);
            link.style.display = 'none';
            document.body.append(link);
            link.click();
            link.remove();
            setTimeout(() => URL.revokeObjectURL(url), 60000);
        },
        files_release: (handle) => context.release(handle),
    };
});
