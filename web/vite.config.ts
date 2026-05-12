import path from 'node:path';
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { TanStackRouterVite } from '@tanstack/router-plugin/vite';

// Vite config for the Sir Thaddeus web workspace.
//
// `base: './'` is critical: the build output ships to `src/Thaddeus.Runtime/wwwroot/`
// and is served from arbitrary loopback ports. Asset URLs must be relative.
export default defineConfig({
  plugins: [
    TanStackRouterVite({ routesDirectory: './src/routes', generatedRouteTree: './src/routeTree.gen.ts' }),
    react(),
  ],
  base: './',
  resolve: {
    alias: {
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
