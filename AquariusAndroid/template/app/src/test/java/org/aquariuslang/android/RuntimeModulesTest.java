package org.aquariuslang.android;

import static org.junit.Assert.*;

import java.util.concurrent.atomic.AtomicInteger;
import org.junit.Test;

public class RuntimeModulesTest {
  @Test
  public void relativeImportsShareIdentityAndCompileOnce() throws Exception {
    var modules = new RuntimeModules<Object>();
    var calls = new AtomicInteger();
    RuntimeModules.Loader<Object, Exception> loader =
        path -> {
          calls.incrementAndGet();
          return new Object();
        };
    assertSame(modules.resolve("./values.mjs", loader), modules.resolve("values.mjs", loader));
    assertSame(modules.resolve("./values.mjs", loader), modules.resolve("./values.mjs", loader));
    assertEquals(1, calls.get());
  }

  @Test
  public void differentModulesRetainDistinctExports() throws Exception {
    var modules = new RuntimeModules<Object>();
    assertNotSame(
        modules.resolve("a.mjs", path -> new Object()),
        modules.resolve("b.mjs", path -> new Object()));
  }

  @Test
  public void invalidPathsNeverReachTheAssetLoader() throws Exception {
    var modules = new RuntimeModules<Object>();
    for (String name :
        new String[] {
          "../values.mjs", "a/b.mjs", "a\\b.mjs", "file://values.mjs", "values.js", "././values.mjs"
        }) {
      assertNull(
          modules.resolve(
              name,
              path -> {
                throw new AssertionError(path);
              }));
    }
  }

  @Test
  public void failedCompilationCanBeRetried() throws Exception {
    var modules = new RuntimeModules<Object>();
    try {
      modules.resolve(
          "a.mjs",
          path -> {
            throw new IllegalStateException("bad module");
          });
      fail();
    } catch (IllegalStateException expected) {
    }
    assertNotNull(modules.resolve("a.mjs", path -> new Object()));
  }
}
