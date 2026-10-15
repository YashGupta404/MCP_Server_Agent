import { ChangeDetectorRef, Component, NgZone, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { TranslateService } from '@ngx-translate/core';
import { SatTagCategory } from '@hylandsoftware/satori-ui/tag';
import { BffApiService } from '../../services/bff/bff-api.service';
import { SystemConfigService } from '../../services/system-config.service';
import { SystemConfig } from '../../types/system-config.model';

@Component({
  selector: 'agent-ecm-picker',
  templateUrl: './ecm-picker.view.html',
  styleUrls: ['./ecm-picker.view.scss'],
  standalone: false,
})
export class EcmPickerView implements OnInit {
  profileInitials = '--';
  loading = true;
  error = '';
  configs: SystemConfig[] = [];
  active = '';
  selecting = '';
  connected = '';

  readonly skeletons = [1, 2, 3, 4, 5];

  constructor(
    private readonly bff: BffApiService,
    private readonly systems: SystemConfigService,
    private readonly router: Router,
    private readonly translate: TranslateService,
    private readonly zone: NgZone,
    private readonly cdr: ChangeDetectorRef,
  ) {}

  async ngOnInit(): Promise<void> {
    void this.loadProfile();
    try {
      const { configs, active } = await this.systems.list();
      this.configs = configs;
      this.active = active;
    } catch (e: any) {
      this.error = e?.message || this.translate.instant('ecm.loadError');
    } finally {
      // chrome.storage/fetch continuations can land outside Angular's zone — force a tick so the
      // loaded list actually renders instead of staying on the skeletons.
      this.zone.run(() => {
        this.loading = false;
        this.cdr.detectChanges();
      });
    }
  }

  private async loadProfile(): Promise<void> {
    try {
      const profile = await this.bff.getProfile();
      this.profileInitials = profile.initials || '--';
      this.cdr.detectChanges();
    } catch {
      /* non-fatal */
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
      this.error = e?.message || this.translate.instant('ecm.connectError');
    } finally {
      this.selecting = '';
    }
  }

  badgeCategory(systemType: string): SatTagCategory {
    const t = (systemType || '').toLowerCase();
    if (t.includes('onbase')) return 'blue';
    if (t.includes('cic')) return 'green';
    if (t.includes('nuxeo')) return 'teal';
    return 'gray';
  }

  close(): void {
    window.close();
  }
}
