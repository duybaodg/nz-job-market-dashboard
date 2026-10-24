import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    // Round 19: keep the React runtime in its own cached vendor chunk and the
    // charting stack (only reached through the lazy imports in the trends view)
    // separate from application code. This is what removes the single >500 kB
    // entry chunk without changing any runtime behaviour.
    rolldownOptions: {
      output: {
        codeSplitting: {
          groups: [
            {
              name: 'react-vendor',
              test: /node_modules[\\/](?:react|react-dom|scheduler)[\\/]/,
              priority: 20,
            },
            {
              name: 'charts',
              test: /node_modules[\\/](?:recharts|victory-vendor|d3-[^\\/]+|react-smooth|react-transition-group|redux|@reduxjs|immer|reselect|decimal\.js-light)[\\/]/,
              priority: 10,
            },
          ],
        },
      },
    },
  },
  test: {
    environment: 'node',
    include: ['src/**/*.test.{ts,tsx}'],
  },
})
