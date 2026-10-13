import { Injectable } from '@angular/core';
import { SystemConfig } from '../types/system-config.model';
import { BffApiService } from './bff/bff-api.service';
import { Settings } from './settings';

const ext: any = (globalThis as any).chrome ?? (globalThis as any).browser;
const PICKED_KEY = 'uceb_picked_system';

// Lists the ECM system configurations and sets the active one for the session (BFF /api/system-configs).
@Injectable({ providedIn: 'root' })
export class SystemConfigService {
  constructor(private readonly bff: BffApiService) {}

  async list(): Promise<{ configs: SystemConfig[]; active: string }> {
    const data = await this.bff.get<any>('/api/system-configs');
    return {
      configs: Array.isArray(data.configs) ? data.configs : [],
      active: data.active ?? '',
    };
  }

  async setActive(friendlyName: string): Promise<string> {
    const data = await this.bff.post<any>('/api/system-config', { friendlyName });
    const active = data.active ?? friendlyName;
    await ext?.storage?.local?.set?.({ [PICKED_KEY]: active });
    return active;
  }

  // DEV ONLY: the system the user last connected to, so a rebuild/reload can go straight to the
  // documents view instead of making them pick again.
  async rememberedSystem(): Promise<string | null> {
    if (!Settings.config?.devKeepSession) return null;
    const r = await ext?.storage?.local?.get?.(PICKED_KEY);
    return r?.[PICKED_KEY] ?? null;
  }

  // Re-applies the remembered system on the MCP (which resets to its default on restart).
  async restoreRemembered(): Promise<string | null> {
    const name = await this.rememberedSystem();
    if (!name) return null;
    try {
      return await this.setActive(name);
    } catch {
      return null;
    }
  }
}
