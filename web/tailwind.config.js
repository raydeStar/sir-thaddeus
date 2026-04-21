/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        // Warm-neutral palette, Apple/Anthropic sensibility. Single accent.
        canvas: {
          DEFAULT: '#FAFAF7',
          raised: '#FFFFFF',
          sunken: '#F5F4F0',
        },
        ink: {
          DEFAULT: '#161514',
          muted: '#6E6B65',
          subtle: '#9C988F',
        },
        line: {
          DEFAULT: '#ECEAE3',
          strong: '#DAD7CE',
        },
        accent: {
          DEFAULT: '#1F1E1C',
          soft: '#F0EDE5',
          ring: '#1F1E1C',
        },
        // Legacy keys kept so older code doesn't break mid-refactor.
        thaddeus: {
          ink: '#161514',
          mist: '#F5F4F0',
          accent: '#1F1E1C',
          warn: '#B91C1C',
        },
      },
      fontFamily: {
        sans: [
          'Inter',
          '-apple-system',
          'BlinkMacSystemFont',
          'Segoe UI',
          'Roboto',
          'Helvetica Neue',
          'Arial',
          'sans-serif',
        ],
        serif: ['ui-serif', 'Georgia', 'Cambria', 'serif'],
        mono: ['ui-monospace', 'SFMono-Regular', 'Menlo', 'Consolas', 'monospace'],
      },
      fontSize: {
        xs: ['0.75rem', { lineHeight: '1.1rem' }],
        sm: ['0.8125rem', { lineHeight: '1.25rem' }],
        base: ['0.9375rem', { lineHeight: '1.5rem' }],
        lg: ['1.0625rem', { lineHeight: '1.6rem' }],
        xl: ['1.25rem', { lineHeight: '1.75rem' }],
        '2xl': ['1.625rem', { lineHeight: '2rem' }],
        '3xl': ['2.125rem', { lineHeight: '2.5rem' }],
      },
      letterSpacing: {
        tightest: '-0.02em',
      },
      boxShadow: {
        soft: '0 1px 2px rgba(20, 18, 14, 0.04), 0 0 0 1px rgba(20, 18, 14, 0.04)',
        lift: '0 6px 24px -8px rgba(20, 18, 14, 0.12), 0 2px 6px rgba(20, 18, 14, 0.04)',
      },
      borderRadius: {
        xl: '0.875rem',
        '2xl': '1.125rem',
      },
    },
  },
  plugins: [],
};
