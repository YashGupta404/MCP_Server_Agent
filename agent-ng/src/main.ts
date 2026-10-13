import { platformBrowserDynamic } from '@angular/platform-browser-dynamic';

import { AppModule } from './app/app.module';
import { AppConfigService } from './app/services/app-config.service';

// Load runtime config (configs.json) into Settings.config BEFORE bootstrapping (OIFS-style), so
// endpoints/auth aren't baked into the bundle.
new AppConfigService()
  .appConfig$()
  .then(() =>
    platformBrowserDynamic()
      .bootstrapModule(AppModule, { ngZoneEventCoalescing: true })
      .catch((err) => console.error(err)),
  )
  .catch((err) => console.error('App bootstrap failed; config could not load', err));
