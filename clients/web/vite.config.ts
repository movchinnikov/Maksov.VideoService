import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// In dev mode the API is proxied: the browser talks to the same origin, so no CORS is needed for the API.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': 'http://localhost:8081',
    },
  },
  build: {
    target: 'es2023',
    sourcemap: true,
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
