import {
  checkBytes,
  storageKey,
  archiveName,
  normalize,
} from "./application-core.mjs";
export class BrowserStorage {
  constructor(namespace) {
    this.name = "aquarius:" + namespace;
    this.opening = null;
  }
  async database() {
    if (!globalThis.indexedDB)
      throw new Error("Unsupported: IndexedDB persistent storage unavailable");
    this.opening ??= new Promise((resolve, reject) => {
      const request = indexedDB.open(this.name, 1);
      request.onupgradeneeded = () =>
        request.result.createObjectStore("resources");
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
      request.onblocked = () =>
        reject(
          new Error("Unavailable: Storage upgrade blocked by another tab"),
        );
    });
    return this.opening;
  }
  async transaction(mode, action, signal) {
    signal?.throwIfAborted();
    const db = await this.database();
    signal?.throwIfAborted();
    return new Promise((resolve, reject) => {
      const tx = db.transaction("resources", mode),
        abort = () => {
          try {
            tx.abort();
          } catch {}
        },
        request = action(tx.objectStore("resources"));
      let result;
      signal?.addEventListener("abort", abort, { once: true });
      request.onsuccess = () => (result = request.result);
      tx.oncomplete = () => {
        signal?.removeEventListener("abort", abort);
        resolve(result);
      };
      tx.onabort = tx.onerror = () => {
        signal?.removeEventListener("abort", abort);
        reject(
          signal?.aborted
            ? signal.reason
            : (tx.error ?? new Error("Persistent transaction failed")),
        );
      };
    });
  }
  get(key, signal) {
    return this.transaction("readonly", (s) => s.get(key), signal);
  }
  put(key, value, signal) {
    return this.transaction("readwrite", (s) => s.put(value, key), signal);
  }
  delete(key, signal) {
    return this.transaction("readwrite", (s) => s.delete(key), signal);
  }
  keys(signal) {
    return this.transaction("readonly", (s) => s.getAllKeys(), signal);
  }
  async transfer(from, to, overwrite, move, signal) {
    if (to === from || to.startsWith(from + "/") || from.startsWith(to + "/"))
      throw new Error("Conflict: Overlapping resource locations");
    signal?.throwIfAborted();
    const db = await this.database();
    signal?.throwIfAborted();
    return new Promise((resolve, reject) => {
      const tx = db.transaction("resources", "readwrite"),
        store = tx.objectStore("resources"),
        request = store.openCursor(),
        source = [],
        abort = () => tx.abort();
      let destination, failure;
      signal?.addEventListener("abort", abort, { once: true });
      request.onsuccess = () => {
        const cursor = request.result;
        if (cursor) {
          if (cursor.key === to) destination = cursor.value;
          if (
            cursor.key === from ||
            (typeof cursor.key === "string" &&
              cursor.key.startsWith(from + "/"))
          )
            source.push([cursor.key, cursor.value]);
          cursor.continue();
          return;
        }
        try {
          const root = source.find(([key]) => key === from)?.[1];
          if (!root) throw new Error("NotFound: Source missing");
          if (
            destination &&
            (!overwrite || root.directory || destination.directory)
          )
            throw new Error("Conflict: Destination exists");
          if (source.length > 4096)
            throw new Error("LimitExceeded: Resource entries");
          checkBytes(
            source.reduce(
              (n, [, record]) => n + (record.bytes?.length ?? 0),
              0,
            ),
          );
          for (const [key, value] of source) {
            store.put(value, to + key.slice(from.length));
            if (move) store.delete(key);
          }
        } catch (error) {
          failure = error;
          tx.abort();
        }
      };
      const cleanup = () => signal?.removeEventListener("abort", abort);
      tx.oncomplete = () => {
        cleanup();
        resolve();
      };
      tx.onerror = tx.onabort = () => {
        cleanup();
        reject(signal?.aborted ? signal.reason : (failure ?? tx.error));
      };
    });
  }
  async read(key, signal) {
    const record = await this.get("settings/" + storageKey(key), signal);
    return record?.bytes ?? null;
  }
  write(key, data, signal) {
    checkBytes(data.length);
    return this.put(
      "settings/" + storageKey(key),
      { bytes: data.slice(), modified: Date.now() },
      signal,
    );
  }
  remove(key, signal) {
    return this.delete("settings/" + storageKey(key), signal);
  }
  async dispose() {
    if (this.opening)
      try {
        (await this.opening).close();
      } catch {}
    this.opening = null;
  }
}
export class BrowserFiles {
  constructor(storage, download) {
    this.storage = storage;
    this.download = download;
    this.id = "browser-" + crypto.randomUUID();
    this.entries = new Map();
    this.temporary = new Set();
    this.next = 0;
  }
  get capabilities() {
    return {
      paths: false,
      streams: true,
      seek: true,
      directories: true,
      atomicReplace: !!globalThis.indexedDB,
      persistent: !!globalThis.indexedDB,
    };
  }
  register(source, name, kind = "File") {
    const resource = {
      provider: this.id,
      id: "selected:" + ++this.next,
      name,
      kind,
    };
    this.entries.set(resource.id, { resource, source });
    return resource;
  }
  resolve(location) {
    if (typeof location !== "string")
      throw new Error("Expected resource location");
    const match = /^(persistent|download|temporary):\/?(.*)$/.exec(location);
    if (!match)
      throw new Error(
        "Unsupported: Use a selected resource, persistent:/name or download:/name",
      );
    const path = match[2];
    archiveName(path);
    if (match[1] === "temporary" && !this.temporary.has(path.split("/")[0]))
      throw new Error("NotFound: Temporary resource lease has ended");
    const id = match[1] + ":/" + path;
    if (this.entries.has(id)) return this.entries.get(id).resource;
    const resource = {
      provider: this.id,
      id,
      name: path.split("/").at(-1),
      kind: "File",
      persistentIdentity:
        match[1] === "persistent" ? this.storage.name + ":" + path : null,
    };
    this.entries.set(resource.id, {
      resource,
      path,
      backend: match[1],
      temporary: match[1] === "temporary",
    });
    return resource;
  }
  owned(resource) {
    const entry = this.entries.get(resource?.id);
    if (resource?.provider !== this.id || entry?.resource !== resource)
      throw new Error("Expected resource owned by this browser provider");
    return entry;
  }
  async stat(resource, signal) {
    signal?.throwIfAborted();
    const e = this.owned(resource);
    if (e.temporary && e.directory)
      return { exists: true, kind: "Directory", length: null, modified: null };
    if (e.backend === "persistent") {
      const value = await this.storage.get(e.path, signal);
      return {
        exists: !!value,
        kind: value?.directory ? "Directory" : "File",
        length: value?.bytes?.length ?? null,
        modified: value?.modified
          ? new Date(value.modified).toISOString()
          : null,
      };
    }
    if (e.source?.kind === "directory")
      return { exists: true, kind: "Directory", length: null, modified: null };
    const file = e.source?.getFile ? await e.source.getFile() : e.source;
    return {
      exists: !!(file || e.bytes),
      kind: "File",
      length: file?.size ?? e.bytes?.length ?? null,
      modified: file?.lastModified
        ? new Date(file.lastModified).toISOString()
        : null,
    };
  }
  async read(resource, signal) {
    signal?.throwIfAborted();
    const e = this.owned(resource);
    if (e.backend === "persistent") {
      const record = await this.storage.get(e.path, signal);
      if (!record || record.directory)
        throw new Error("NotFound: File resource missing");
      checkBytes(record.bytes.length);
      return record.bytes.slice();
    }
    const file = e.source?.getFile ? await e.source.getFile() : e.source;
    if (file) {
      checkBytes(file.size);
      const bytes = new Uint8Array(await file.arrayBuffer());
      signal?.throwIfAborted();
      return bytes;
    }
    if (e.bytes) return e.bytes.slice();
    throw new Error("NotFound: Resource has no data");
  }
  async replace(resource, data, atomic = false, signal) {
    signal?.throwIfAborted();
    checkBytes(data.length);
    const e = this.owned(resource);
    if (e.backend === "persistent") {
      await this.storage.put(
        e.path,
        { bytes: data.slice(), modified: Date.now() },
        signal,
      );
      return true;
    }
    if (e.source?.createWritable) {
      const writer = await e.source.createWritable();
      try {
        await writer.write(data);
        signal?.throwIfAborted();
        await writer.close();
        return true;
      } catch (error) {
        await writer.abort().catch(() => {});
        throw error;
      }
    }
    if (e.source)
      throw new Error("PermissionDenied: Selected File is read-only");
    if (atomic && !e.temporary)
      throw new Error(
        "Unsupported: Downloads cannot guarantee atomic replacement",
      );
    e.bytes = data.slice();
    if (e.backend === "download")
      this.download(resource.name, new Blob([data]));
    return !!e.temporary;
  }
  async directory(resource, signal) {
    const e = this.owned(resource);
    if (e.backend !== "persistent" && !e.temporary)
      throw new Error("Unsupported: Create directories in persistent storage");
    if (e.backend === "persistent")
      await this.storage.put(
        e.path,
        { directory: true, modified: Date.now() },
        signal,
      );
    else e.directory = true;
  }
  async enumerate(resource, signal) {
    const e = this.owned(resource),
      result = [];
    if (e.temporary && e.directory) {
      const prefix = e.path + "/";
      return [...this.entries.values()]
        .filter(
          (item) =>
            item.path?.startsWith(prefix) &&
            !item.path.slice(prefix.length).includes("/"),
        )
        .map((item) => item.resource);
    }
    if (e.source?.kind === "directory") {
      for await (const [name, handle] of e.source.entries()) {
        signal?.throwIfAborted();
        result.push(
          this.register(
            handle,
            name,
            handle.kind === "directory" ? "Directory" : "File",
          ),
        );
        if (result.length > 4096)
          throw new Error("LimitExceeded: Directory entries");
      }
      return result;
    }
    if (e.backend !== "persistent")
      throw new Error("Unsupported: Resource is not a persistent directory");
    const prefix = e.path.replace(/\/$/, "") + "/";
    for (const key of await this.storage.keys(signal)) {
      if (
        typeof key !== "string" ||
        !key.startsWith(prefix) ||
        key.slice(prefix.length).includes("/")
      )
        continue;
      const r = this.resolve("persistent:/" + key),
        meta = await this.stat(r, signal);
      r.kind = meta.kind;
      result.push(r);
      if (result.length > 4096)
        throw new Error("LimitExceeded: Directory entries");
    }
    return result;
  }
  async remove(resource, recursive = false, signal) {
    const e = this.owned(resource);
    signal?.throwIfAborted();
    if (e.backend === "persistent") {
      const keys = (await this.storage.keys(signal)).filter(
        (k) => typeof k === "string" && k.startsWith(e.path + "/"),
      );
      if (keys.length && !recursive)
        throw new Error("Conflict: Directory not empty");
      for (const key of keys) await this.storage.delete(key, signal);
      await this.storage.delete(e.path, signal);
      return;
    }
    if (e.temporary) {
      const children = [...this.entries.values()].filter((item) =>
        item.path?.startsWith(e.path + "/"),
      );
      if (children.length && !recursive)
        throw new Error("Conflict: Directory not empty");
      for (const child of children) this.entries.delete(child.resource.id);
      this.entries.delete(resource.id);
      this.temporary.delete(e.path);
      return;
    }
    if (e.source?.remove) {
      await e.source.remove({ recursive });
      return;
    }
    if (e.source)
      throw new Error("Unsupported: Browser cannot delete selected files");
    this.entries.delete(resource.id);
  }
  async copy(source, destination, overwrite, signal) {
    const from = this.owned(source),
      to = this.owned(destination);
    if (from.backend === "persistent" && to.backend === "persistent")
      return this.storage.transfer(
        from.path,
        to.path,
        overwrite,
        false,
        signal,
      );
    if ((await this.stat(destination, signal)).exists && !overwrite)
      throw new Error("Conflict: Destination exists");
    return this.replace(
      destination,
      await this.read(source, signal),
      false,
      signal,
    );
  }
  async move(source, destination, overwrite, signal) {
    const from = this.owned(source),
      to = this.owned(destination);
    if (from.backend !== "persistent" || to.backend !== "persistent")
      throw new Error("Unsupported: Move requires two persistent resources");
    await this.storage.transfer(from.path, to.path, overwrite, true, signal);
  }
  temporaryResource(directory) {
    const name = crypto.randomUUID();
    this.temporary.add(name);
    const r = this.resolve("temporary:/" + name),
      e = this.owned(r);
    e.temporary = true;
    e.directory = directory;
    e.bytes = new Uint8Array();
    r.kind = directory ? "Directory" : "File";
    return r;
  }
  async open(resource, mode, signal) {
    const e = this.owned(resource);
    mode = mode.toLowerCase();
    if (!["read", "create", "openwrite", "append"].includes(mode))
      throw new Error("Invalid open mode");
    if (mode !== "read" && e.source && !e.source.createWritable)
      throw new Error("PermissionDenied: Resource is read-only");
    let data =
        mode === "create"
          ? new Uint8Array()
          : await this.read(resource, signal),
      position = mode === "append" ? data.length : 0,
      closed = false,
      dirty = mode === "create";
    const live = () => {
      if (closed) throw new Error("Stream is closed");
      signal?.throwIfAborted();
    };
    return {
      canRead: mode === "read",
      canWrite: mode !== "read",
      canSeek: true,
      Read: (count) => {
        live();
        if (mode !== "read") throw new Error("Stream not readable");
        checkBytes(count);
        const result = data.slice(position, position + count);
        position += result.length;
        return result;
      },
      Write: (bytes) => {
        live();
        if (mode === "read") throw new Error("Stream not writable");
        const length = Math.max(data.length, position + bytes.length);
        checkBytes(length);
        const out = new Uint8Array(length);
        out.set(data);
        out.set(bytes, position);
        data = out;
        position += bytes.length;
        dirty = true;
      },
      Seek: (offset, origin) => {
        live();
        if (!Number.isSafeInteger(offset))
          throw new Error("Invalid seek offset");
        const origins = { begin: 0, current: position, end: data.length };
        if (!(origin.toLowerCase() in origins))
          throw new Error("Invalid seek origin");
        const next = origins[origin.toLowerCase()] + offset;
        if (next < 0 || (mode === "append" && next < data.length))
          throw new Error("Invalid seek position");
        checkBytes(next);
        position = next;
        return position;
      },
      Flush: async () => {
        live();
        if (dirty) {
          await this.replace(resource, data, false, signal);
          dirty = false;
        }
      },
      Close: async () => {
        if (closed) return;
        live();
        if (dirty) await this.replace(resource, data, false, signal);
        closed = true;
      },
      Abort: () => {
        closed = true;
        data = new Uint8Array();
        dirty = false;
      },
    };
  }
  dispose() {
    this.entries.clear();
    this.temporary.clear();
  }
}
