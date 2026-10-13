import { AppConfig } from '../types/app-config.model';

// Static reference to runtime config, populated by AppConfigService before bootstrap.
// Mirrors OIFS's Settings class so any service/component can read config without DI plumbing.
export class Settings {
  private constructor() {}
  static config: AppConfig = {} as AppConfig;
}
