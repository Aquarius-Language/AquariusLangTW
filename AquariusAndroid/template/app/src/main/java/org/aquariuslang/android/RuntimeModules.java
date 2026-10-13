package org.aquariuslang.android;

import java.util.HashMap;

/** Flat deployment module identity is independent of the import's relative spelling. */
final class RuntimeModules<T> {
  interface Loader<T, E extends Exception> {
    T load(String path) throws E;
  }

  private final HashMap<String, T> modules = new HashMap<>();

  <E extends Exception> T resolve(String name, Loader<T, E> loader) throws E {
    String path = name.startsWith("./") ? name.substring(2) : name;
    if (!path.matches("[A-Za-z0-9_-][A-Za-z0-9_.-]*\\.mjs")) return null;
    T existing = modules.get(path);
    if (existing != null) return existing;
    T compiled = loader.load(path);
    modules.put(path, compiled);
    return compiled;
  }
}
