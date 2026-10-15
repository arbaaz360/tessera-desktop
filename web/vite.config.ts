import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { fileURLToPath, URL } from 'node:url';

/**
 * No proxy. The app talks to the core by its absolute origin, learned at
 * boot (see `src/shared/api/core.ts`): in Tauri from the shell, in the
 * browser from `.env.local`. The core allows the Vite origin when started
 * with TESSERA_DEV=1 (contract §1); the mock allows it always.
 *
 * Port 5173 is the one the contract names as the dev origin, so it is fixed:
 * a dev server that silently moved to 5174 would be refused by CORS and look
 * like a broken core.
 */
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    port: 5173,
    strictPort: true,
  },
  build: {
    // Surfacing chunk growth early is cheaper than diagnosing a slow first
    // paint later. This app's whole premise is being fast.
    chunkSizeWarningLimit: 600,
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
  },
});
