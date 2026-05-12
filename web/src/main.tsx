import './styles/globals.css';
import React from 'react';
import ReactDOM from 'react-dom/client';
import { RouterProvider, createRouter } from '@tanstack/react-router';
import { routeTree } from './routeTree';
import { applyTheme, readThemePreference } from './lib/theme';

// Apply theme before React mounts so the first paint matches the user's
// preference (no flash of light theme on a dark-mode-preferred boot).
applyTheme(readThemePreference());

const router = createRouter({ routeTree });

const rootEl = document.getElementById('root');
if (!rootEl) throw new Error('Missing #root element');

ReactDOM.createRoot(rootEl).render(
  <React.StrictMode>
    <RouterProvider router={router} />
  </React.StrictMode>,
);
