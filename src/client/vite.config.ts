import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

/**
 * The SPA is built straight into the server's wwwroot.
 *
 * One artifact, one process, no sidecar to deploy — which is what makes "copy one file and run it"
 * possible. The dev proxy exists only so `npm run dev` can talk to a server started separately.
 */
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../Server/wwwroot',
    emptyOutDir: true,
    sourcemap: true,
    rollupOptions: {
      output: {
        // The editor and Mermaid are the two large dependencies, and a reader needs neither.
        // Splitting them out is what keeps first contentful paint on a mid-tier phone inside its
        // budget. Function form because Vite 8's bundler only accepts that.
        manualChunks(id: string) {
          if (id.includes('@milkdown') || id.includes('prosemirror')) {
            return 'editor';
          }
          if (id.includes('mermaid') || id.includes('cytoscape') || id.includes('dagre')) {
            return 'diagrams';
          }
          return undefined;
        },
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:8080',
      '/health': 'http://localhost:8080',
      '/ready': 'http://localhost:8080',
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
  },
});
