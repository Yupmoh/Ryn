import { defineConfig } from "vite";

export default defineConfig({
  base: "./",
  build: {
    outDir: "../wwwroot",
    emptyOutDir: true,
    sourcemap: false,
    rollupOptions: {
      output: {
        entryFileNames: "assets/terminal-app.js",
        chunkFileNames: "assets/[name].js",
        assetFileNames: (asset) => asset.name?.endsWith(".css")
          ? "assets/terminal-app.css"
          : "assets/[name][extname]",
      },
    },
  },
});
