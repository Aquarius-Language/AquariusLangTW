package org.aquariuslang.android;

import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.io.ByteArrayOutputStream;
import java.util.ArrayList;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import org.json.JSONObject;

/** Runs with the Android application's UID and sandbox; no root or external terminal app. */
final class TerminalProcesses {
  private static String capture(InputStream input) {
    var bytes = new ByteArrayOutputStream();
    try (input) {
      byte[] buffer = new byte[8192]; int count;
      while ((count = input.read(buffer)) >= 0) {
        int retained = Math.min(count, 1048576 - bytes.size());
        if (retained > 0) bytes.write(buffer, 0, retained);
      }
      return bytes.toString(StandardCharsets.UTF_8.name());
    } catch (java.io.InterruptedIOException e) {
      // Android closes the process pipes during destroyForcibly(). Preserve the
      // captured prefix instead of replacing an ordinary timeout with an error.
      return new String(bytes.toByteArray(), StandardCharsets.UTF_8);
    } catch (Exception e) { throw new IllegalStateException(e); }
  }
  static String run(String request) throws Exception {
    var json = new JSONObject(request); int timeout = json.getInt("timeoutMs");
    if (timeout < 1 || timeout > 3600000) throw new IllegalArgumentException("Invalid timeout");
    var args = new ArrayList<String>(); args.add(json.getString("executable"));
    var list = json.getJSONArray("args"); for(int i=0;i<list.length();i++)args.add(list.getString(i));
    var builder = new ProcessBuilder(args);
    if(json.has("workingDirectory"))builder.directory(new java.io.File(json.getString("workingDirectory")));
    var process = builder.start(); process.getOutputStream().close();
    var stdout = CompletableFuture.supplyAsync(() -> capture(process.getInputStream()));
    var stderr = CompletableFuture.supplyAsync(() -> capture(process.getErrorStream()));
    try {
      boolean timedOut = !process.waitFor(timeout, TimeUnit.MILLISECONDS);
      if(timedOut){process.destroyForcibly();process.waitFor();}
      return new JSONObject().put("exitCode",timedOut?-1:process.exitValue()).put("standardOutput",stdout.get(timeout,TimeUnit.MILLISECONDS)).put("standardError",stderr.get(timeout,TimeUnit.MILLISECONDS)).put("timedOut",timedOut).toString();
    } finally { process.destroy(); }
  }
}
