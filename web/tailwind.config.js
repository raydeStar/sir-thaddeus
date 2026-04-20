/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        // Phase-1 placeholder palette. Real design tokens land with Phase 8 polish.
        thaddeus: {
          ink: '#0f172a',
          mist: '#f1f5f9',
          accent: '#2563eb',
          warn: '#b91c1c',
        },
      },
    },
  },
  plugins: [],
};
