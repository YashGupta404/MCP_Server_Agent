import { Component, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { TranslateService } from '@ngx-translate/core';
import { AgentAuthService } from '../../services/auth/agent-auth.service';
import { SystemConfigService } from '../../services/system-config.service';

@Component({
  selector: 'agent-sign-in',
  templateUrl: './sign-in.view.html',
  styleUrls: ['./sign-in.view.scss'],
  standalone: false,
})
export class SignInView implements OnInit {
  busy = false;
  error = '';

  constructor(
    private readonly auth: AgentAuthService,
    private readonly systems: SystemConfigService,
    private readonly router: Router,
    private readonly translate: TranslateService,
  ) {}

  async ngOnInit(): Promise<void> {
    // Panel reopened while already signed in -> skip straight to the content-system picker.
    if (!(await this.auth.getSession())) return;
    // Dev loop: if a system was already chosen, re-apply it and go straight to the documents view.
    if (await this.systems.restoreRemembered()) {
      this.router.navigate(['/documents']);
      return;
    }
    this.router.navigate(['/systems']);
  }

  async signIn(): Promise<void> {
    this.error = '';
    this.busy = true;
    try {
      await this.auth.login();
      this.router.navigate(['/systems']);
    } catch (e: any) {
      this.error = e?.message || this.translate.instant('signIn.error');
    } finally {
      this.busy = false;
    }
  }
}
