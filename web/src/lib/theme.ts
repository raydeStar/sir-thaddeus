export type ThemePreference = 'light' | 'dark' | 'system';

const STORAGE_KEY = 'thaddeus.theme';

export function readThemePreference(): ThemePreference {
  if (typeof window === 'undefined') return 'system';
  try {
    const v = window.localStorage.getItem(STORAGE_KEY);
    if (v === 'light' || v === 'dark' || v === 'system') return v;
  } catch {
    /* localStorage may be blocked; fall through */
  }
  return 'system';
}

function prefersDark(): boolean {
  if (typeof window === 'undefined' || !window.matchMedia) return false;
  return window.matchMedia('(prefers-color-scheme: dark)').matches;
}

export function applyTheme(pref: ThemePreference): void {
  if (typeof document === 'undefined') return;
  const root = document.documentElement;
  root.dataset.theme = pref;
  const effectiveDark = pref === 'dark' || (pref === 'system' && prefersDark());
  root.classList.toggle('dark', effectiveDark);
}

export function writeThemePreference(pref: ThemePreference): void {
  if (typeof window !== 'undefined') {
    try {
      window.localStorage.setItem(STORAGE_KEY, pref);
    } catch {
      /* ignore */
    }
  }
  applyTheme(pref);
}

/**
 * Subscribe to OS theme changes so "system" mode tracks the OS without
 * requiring the user to toggle anything.
 */
export function watchSystemTheme(getCurrent: () => ThemePreference): () => void {
  if (typeof window === 'undefined' || !window.matchMedia) return () => undefined;
  const media = window.matchMedia('(prefers-color-scheme: dark)');
  const listener = () => {
    if (getCurrent() === 'system') applyTheme('system');
  };
  media.addEventListener('change', listener);
  return () => media.removeEventListener('change', listener);
}
