import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterModule, Routes } from '@angular/router';
import { SignInView } from './sign-in.view';

const ROUTES: Routes = [{ path: '', component: SignInView }];

@NgModule({
  declarations: [SignInView],
  imports: [CommonModule, FormsModule, RouterModule.forChild(ROUTES)],
})
export class SignInModule {}
