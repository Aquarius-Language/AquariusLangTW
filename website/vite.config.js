import { resolve } from "node:path";
import { defineConfig } from "vite";

export default defineConfig({
  base: "./",
  build: {
    rollupOptions: {
      input: {
        main: resolve(import.meta.dirname, "index.html"),
        tutorial: resolve(import.meta.dirname, "tutorial.html"),
      },
      output: {
        manualChunks(id) {
          if (id.includes("/three/src/") || id.includes("/three/build/"))
            return "three-core";
          if (id.includes("/three/examples/")) return "three-effects";
        },
      },
    },
  },
});
