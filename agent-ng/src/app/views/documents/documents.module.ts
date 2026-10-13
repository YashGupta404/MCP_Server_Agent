import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterModule, Routes } from '@angular/router';
import { DocumentsView } from './documents.view';

const ROUTES: Routes = [{ path: '', component: DocumentsView }];

@NgModule({
  declarations: [DocumentsView],
  imports: [CommonModule, FormsModule, RouterModule.forChild(ROUTES)],
})
export class DocumentsModule {}
