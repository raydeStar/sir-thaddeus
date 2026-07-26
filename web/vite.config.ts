import path from 'node:path';
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Vite config for the Sir Thaddeus web workspace.
//
// The runtime serves the SPA at the loopback origin root. Root-relative asset
// URLs keep direct deep links such as /chat/:threadId and /wiki working after
// a refresh; document-relative URLs would incorrectly request /chat/assets/*.
export default defineConfig({
  plugins: [react()],
  base: '/',
  resolve: {
    alias: {
      '@tanstack/react-router': path.resolve(__dirname, './src/lib/routerShim.tsx'),
      '@': path.resolve(__dirname, './src'),
      '@thaddeus/shared-types': path.resolve(__dirname, '../packages/shared-types/ts/src/index.ts'),
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: true,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (!id.includes('node_modules')) return undefined;
          if (id.includes('@tiptap')) return 'tiptap-vendor';
          if (id.includes('prosemirror')) return 'prosemirror-vendor';
          if (id.includes('marked') || id.includes('turndown') || id.includes('remark')) return 'markdown-vendor';
          if (id.includes('lucide-react')) return 'icons-vendor';
          return undefined;
        },
      },
    },
  },
  server: {
    port: 5173,
    strictPort: true,
  },
});
