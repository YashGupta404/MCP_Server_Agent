import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { RouterModule, Routes } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { EcmPickerView } from './ecm-picker.view';

const ROUTES: Routes = [{ path: '', component: EcmPickerView }];

@NgModule({
  declarations: [EcmPickerView],
  imports: [CommonModule, MatButtonModule, MatIconModule, TranslateModule, RouterModule.forChild(ROUTES)],
})
export class EcmPickerModule {}
