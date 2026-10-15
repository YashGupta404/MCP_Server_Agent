import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { RouterModule, Routes } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { SignInView } from './sign-in.view';

const ROUTES: Routes = [{ path: '', component: SignInView }];

@NgModule({
  declarations: [SignInView],
  imports: [CommonModule, FormsModule, MatButtonModule, MatCardModule, TranslateModule, RouterModule.forChild(ROUTES)],
})
export class SignInModule {}
