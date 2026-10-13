import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { EcmPickerView } from './ecm-picker.view';

const ROUTES: Routes = [{ path: '', component: EcmPickerView }];

@NgModule({
  declarations: [EcmPickerView],
  imports: [CommonModule, RouterModule.forChild(ROUTES)],
})
export class EcmPickerModule {}
