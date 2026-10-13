import { Component, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { SystemConfigService } from '../../services/system-config.service';
import { SystemConfig } from '../../types/system-config.model';

@Component({
  selector: 'agent-ecm-picker',
  templateUrl: './ecm-picker.view.html',
  styleUrls: ['./ecm-picker.view.scss'],
  standalone: false,
})
export class EcmPickerView implements OnInit {
  loading = true;
  error = '';
  configs: SystemConfig[] = [];
  active = '';
  selecting = '';
  connected = '';

  readonly skeletons = [1, 2, 3, 4, 5];

  constructor(private readonly systems: SystemConfigService, private readonly router: Router) {}

  async ngOnInit(): Promise<void> {
    try {
      const { configs, active } = await this.systems.list();
      this.configs = configs;
      this.active = active;
    } catch (e: any) {
      this.error = e?.message || 'Could not load content systems.';
    } finally {
      this.loading = false;
    }
  }

  async choose(c: SystemConfig): Promise<void> {
    if (this.selecting) return;
    this.selecting = c.friendlyName;
    this.error = '';
    try {
      this.active = await this.systems.setActive(c.friendlyName);
      this.connected = this.active;
      this.router.navigate(['/documents']);
    } catch (e: any) {
      this.error = e?.message || 'Could not connect to that system.';
    } finally {
      this.selecting = '';
    }
  }

  badgeClass(systemType: string): string {
    const t = (systemType || '').toLowerCase();
    if (t.includes('onbase')) return 'ecm__badge--onbase';
    if (t.includes('cic')) return 'ecm__badge--cic';
    if (t.includes('nuxeo')) return 'ecm__badge--nuxeo';
    return 'ecm__badge--generic';
  }

  close(): void {
    window.close();
  }
}
