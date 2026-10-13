import { Injectable } from '@angular/core';
import { AppConfig } from '../types/app-config.model';
import { Settings } from './settings';

// Loads /configs.json into Settings.config BEFORE the app bootstraps (see main.ts), so endpoints and
// auth settings are not compiled into the bundle — flip environments by editing the file, no rebuild.
@Injectable({ providedIn: 'root' })
export class AppConfigService {
  async appConfig$(): Promise<AppConfig> {
    try {
      const resp = await fetch('configs.json', { cache: 'no-store' });
      Settings.config = (await resp.json()) as AppConfig;
    } catch (err) {
      console.error('[config] could not load configs.json; using empty defaults', err);
      Settings.config = Settings.config ?? ({} as AppConfig);
    }
    return Settings.config;
  }
}
