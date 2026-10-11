/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// Geliştirmede API'ye aynı origin'den erişmek için proxy (cookie ve CSRF aynı origin varsayar).
const apiTarget = process.env.KULVAR_API ?? 'https://localhost:8843';

export default defineConfig({
  plugins: [react()],
  build: {
    sourcemap: false,
    target: 'es2022',
    // CSP font-src/img-src 'self': küçük varlıklar da data: URI olarak gömülmez, ayrı dosya olarak sunulur.
    assetsInlineLimit: 0,
  },
  server: {
    port: 5183,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: false, secure: false },
      '/health': { target: apiTarget, changeOrigin: false, secure: false },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    css: false,
  },
});
