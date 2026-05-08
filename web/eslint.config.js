// ESLint v9 flat config for the Sir Thaddeus web workspace.
//
// Scope: just the source under `src/`. We deliberately ignore generated
// files (routeTree.gen.ts), build output (dist/), node_modules, and the
// Playwright e2e tests (separate runner, separate type bounds).

import js from '@eslint/js';
import tseslint from 'typescript-eslint';
import react from 'eslint-plugin-react';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';

export default tseslint.config(
  {
    // Hard ignores — everything below will not be linted regardless of
    // command-line invocation.
    ignores: [
      'dist/**',
      'node_modules/**',
      'src/routeTree.gen.ts',
      'tests/e2e/**',
      'playwright.config.ts',
      'postcss.config.js',
      'tailwind.config.js',
      // TypeScript compiles vite.config.ts → vite.config.{js,d.ts}; the
      // emitted JS file confuses ESLint (sees Node globals as undefined).
      // Ignore all vite.config.* variants and only lint sources under src/.
      'vite.config.*',
      'eslint.config.js',
    ],
  },

  // Base recommended rules.
  js.configs.recommended,
  ...tseslint.configs.recommended,

  // TS / TSX source files.
  {
    files: ['src/**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: 'module',
      globals: {
        ...globals.browser,
        ...globals.es2022,
      },
      parserOptions: {
        ecmaFeatures: { jsx: true },
      },
    },
    plugins: {
      react,
      'react-hooks': reactHooks,
    },
    settings: {
      react: { version: 'detect' },
    },
    rules: {
      // React 17+ JSX runtime — `import React` not required.
      'react/react-in-jsx-scope': 'off',
      'react/prop-types': 'off',

      // Hooks rules — the second one (`exhaustive-deps`) catches real bugs.
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'warn',

      // TS handles unused-vars better than the base rule. Allow leading
      // underscore for intentional ignores (matches existing code style:
      // `({ node: _node, ...props })`).
      'no-unused-vars': 'off',
      '@typescript-eslint/no-unused-vars': [
        'warn',
        {
          argsIgnorePattern: '^_',
          varsIgnorePattern: '^_',
          caughtErrorsIgnorePattern: '^_',
          ignoreRestSiblings: true,
        },
      ],

      // The codebase uses `as unknown as T` in a couple of audio paths to
      // cross WebKit prefixes; downgrade to warn so it isn't a CI red.
      '@typescript-eslint/no-explicit-any': 'warn',

      // Empty catch blocks are legitimate here for "best-effort" cleanup
      // (audio teardown, mic stream stop). The codebase already comments
      // them; we don't need ESLint to repeat the lecture.
      'no-empty': ['error', { allowEmptyCatch: true }],

      // Catch accidental `console.log` left in shipping code; allow
      // warn/error/info because we use them deliberately for diagnostics
      // the user can see in DevTools.
      'no-console': ['warn', { allow: ['warn', 'error', 'info'] }],
    },
  },
);
