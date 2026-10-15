import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { fileURLToPath, URL } from 'node:url';

/**
 * The dev server proxies /api to a real Immich instance rather than mocking it.
 *
 * Working against real data from day one surfaces the problems that matter at
 * this library's size — 1.6M assets, 4,878 albums — which a fixture set never
 * would. Point IMMICH_URL at whichever instance you want:
 *
 *   :2283  production (default)
 *   :2284  the v3.1.0 fork sandbox
 *
 * Production is the default because the sandbox cannot serve semantic search.
 * Its database was built with --exclude-table-data='public.smart_search' to get
 * a 32 GB dump down to 2.9 GB, so smart_search holds 0 rows against production's
 * 1,338,913 — and it runs with IMMICH_MACHINE_LEARNING_ENABLED=false, so the API
 * rejects the query outright with "Smart search is not enabled".
 *
 * Both versions return COLUMNAR timeline buckets, so the transposition in
 * shared/api/timeline.ts works against either. Switch to :2284 when testing v3
 * behaviour specifically; expect search to be dead there.
 */
const IMMICH_URL = process.env.IMMICH_URL ?? 'http://127.0.0.1:2283';

/**
 * Optional dev-only API key.
 *
 * The app itself authenticates with a session cookie and deliberately never
 * holds a key in JavaScript — a key in the browser is readable by anything on
 * the page. But during development it is convenient to skip the login screen,
 * so if IMMICH_API_KEY is set the PROXY attaches it server-side.
 *
 * The key therefore lives in the Node process, never in the bundle and never in
 * the browser. This block does not exist in a production build, which serves
 * static files with no proxy at all.
 */
const DEV_API_KEY = process.env.IMMICH_API_KEY;

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    // 3000 is taken by nzbdav on this machine; 4300 is free.
    port: 4300,
    strictPort: true,
    proxy: {
      '/api': {
        target: IMMICH_URL,
        changeOrigin: true,
        ...(DEV_API_KEY ? { headers: { 'x-api-key': DEV_API_KEY } } : {}),
      },
    },
  },
  build: {
    // Surfacing chunk growth early is cheaper than diagnosing a slow first
    // paint later. This app's whole premise is being fast.
    chunkSizeWarningLimit: 600,
  },
  test: {
    environment: 'jsdom',
    globals: true,
  },
});
