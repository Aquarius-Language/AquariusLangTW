// IndexedDB for C/C++ code compiled to WebAssembly: open a database, run a transaction, get/put/delete records and
// list keys. Records are {bytes: Uint8Array, modified: ms} or {directory: true, modified: ms} under string keys.
// Callers decide everything else (paths, conflicts, limits, retries); results return through C callbacks.
import { registerGlue } from '../core/glue.mjs';

registerGlue('storage', (context) => {
    const error = (e) => context.allocate(`${e?.name ?? 'Error'}: ${e?.message ?? String(e)}`)[0];
    const done = (callback, data, ...values) => { context.callPointer(callback, data, ...values); context.wake(); };
    const key = (pointer, length) => context.text(pointer, length);
    return {
        storage_available: () => (globalThis.indexedDB ? 1 : 0),

        /** Opens (creating) database `name` with object store `store`: done(data, handle, error). */
        storage_open(name, nameLength, store, storeLength, callback, data) {
            const storeName = context.text(store, storeLength);
            let request;
            try { request = indexedDB.open(context.text(name, nameLength), 1); }
            catch (e) { done(callback, data, 0, error(e)); return; }
            request.onupgradeneeded = () => request.result.createObjectStore(storeName);
            request.onsuccess = () => done(callback, data, context.retain({ db: request.result, store: storeName }), 0);
            request.onerror = () => done(callback, data, 0, error(request.error));
            request.onblocked = () => done(callback, data, 0, error({ name: 'InvalidStateError', message: 'Storage upgrade blocked by another tab' }));
        },
        storage_close(database) {
            try { context.object(database)?.db.close(); } catch {}
            context.release(database);
        },

        /** A transaction handle (0 on failure); requests issued from its callbacks keep it active. */
        storage_begin(database, write) {
            const { db, store } = context.object(database);
            try {
                const tx = db.transaction(store, write ? 'readwrite' : 'readonly');
                return context.retain({ tx, store: tx.objectStore(store) });
            } catch { return 0; }
        },
        /** done(data, error) once the transaction commits (error 0) or aborts. */
        storage_finish(transaction, callback, data) {
            const t = context.object(transaction);
            let settled = false;
            const finish = (e) => {
                if (settled) return;
                settled = true;
                context.release(transaction);
                done(callback, data, e ? error(e) : 0);
            };
            t.tx.oncomplete = () => finish(null);
            t.tx.onabort = t.tx.onerror = () => finish(t.tx.error ?? { name: 'AbortError', message: 'The transaction was aborted.' });
        },
        storage_abort(transaction) { try { context.object(transaction)?.tx.abort(); } catch {} },

        /** callback(data, found, bytes, length, directory, modified) with bytes from malloc (C frees). */
        storage_get(transaction, keyPointer, keyLength, callback, data) {
            const request = context.object(transaction).store.get(key(keyPointer, keyLength));
            request.onsuccess = () => {
                const value = request.result;
                if (value === undefined || value === null) { context.callPointer(callback, data, 0, 0, 0, 0, -1); return; }
                const [pointer, length] = value.bytes instanceof Uint8Array ? context.allocate(value.bytes) : [0, 0];
                context.callPointer(callback, data, 1, pointer, length, value.directory ? 1 : 0, typeof value.modified === 'number' ? value.modified : -1);
            };
        },
        storage_put(transaction, keyPointer, keyLength, bytes, length, directory, modified) {
            const value = directory ? { directory: true, modified } : { bytes: context.bytes.slice(bytes, bytes + length), modified };
            context.object(transaction).store.put(value, key(keyPointer, keyLength));
        },
        storage_delete(transaction, keyPointer, keyLength) {
            context.object(transaction).store.delete(key(keyPointer, keyLength));
        },
        /** callback(data, keys, length): every string key, NUL-separated, from malloc. */
        storage_keys(transaction, callback, data) {
            const request = context.object(transaction).store.getAllKeys();
            request.onsuccess = () => {
                const keys = request.result.filter((k) => typeof k === 'string');
                const [pointer, length] = context.allocate(keys.length ? keys.join('\0') + '\0' : '');
                context.callPointer(callback, data, pointer, length);
            };
        },
    };
});
