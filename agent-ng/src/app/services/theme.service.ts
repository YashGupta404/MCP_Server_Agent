import { Injectable } from '@angular/core';

export type ThemeMode = 'light' | 'dark';

const STORAGE_KEY = 'agent_theme_mode';

/**
 * Toggles the Satori light/dark theme by switching `color-scheme` on the root element.
 * Satori's MD3 tokens use the CSS `light-dark()` function, so flipping the `dark-theme`
 * class is all that is needed. The choice is persisted in localStorage.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private current: ThemeMode = 'light';

  init(): void {
    const stored = this.readStored();
    const system: ThemeMode = window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    this.set(stored ?? system);
  }

  get mode(): ThemeMode {
    return this.current;
  }

  get isDark(): boolean {
    return this.current === 'dark';
  }

  toggle(): void {
    this.set(this.current === 'dark' ? 'light' : 'dark');
  }

  set(mode: ThemeMode): void {
    this.current = mode;
    document.documentElement.classList.toggle('dark-theme', mode === 'dark');
    try {
      localStorage.setItem(STORAGE_KEY, mode);
    } catch {
      /* storage may be unavailable in the extension context — non-fatal */
    }
  }

  private readStored(): ThemeMode | null {
    try {
      const v = localStorage.getItem(STORAGE_KEY);
      return v === 'light' || v === 'dark' ? v : null;
    } catch {
      return null;
    }
  }
}
