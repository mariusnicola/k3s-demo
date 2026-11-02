import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

const ingress = process.env.BT_INGRESS ?? "http://<INGRESS_IP>";

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      "/api": ingress,
      "/live": ingress,
      "/ingress": ingress,
      "/ws": { target: ingress, ws: true },
    },
  },
  build: {
    rolldownOptions: {
      output: {
        codeSplitting: {
          groups: [
            { name: "react", test: /node_modules[\\/](react|react-dom|react-router|react-router-dom|scheduler|cookie|set-cookie-parser)[\\/]/ },
            { name: "radix", test: /node_modules[\\/](@radix-ui|@floating-ui|react-remove-scroll|react-style-singleton|use-callback-ref|use-sidecar|aria-hidden)/ },
          ],
        },
      },
    },
  },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["src/test/setup.ts"],
    // On a machine under CPU load, each added worker makes every test slower. Finds then outrun the query limit and
    // fail, nearly all in the files that start early in the run, and in any position within the file.
    maxWorkers: 4,
    // On Windows the default forks pool and the module cache write every transformed module to disk. A clean install
    // has nothing cached, so that write and read ran inside a lazy page's first import and outlasted the find wait.
    pool: "threads",
    fsModuleCache: false,
    css: false,
  },
});
